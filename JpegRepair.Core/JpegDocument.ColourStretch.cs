namespace JpegRepair.Core;

/// <summary>
/// A stretch of MCUs (in stream order) whose colour is off from the rows above and below it, found by
/// <see cref="JpegDocument.FindNextColourStretch"/> and fixed by <see cref="JpegDocument.ApplyColourStretch"/>.
/// <see cref="Deltas"/> are added to the DC of every MCU from <see cref="First"/> to <see cref="Last"/>.
/// <see cref="CostBefore"/> and <see cref="CostAfter"/> are the mean seam difference per MCU (in levels) over the
/// stretch.
/// </summary>
public sealed record ColourStretchFix(int First, int Last, IReadOnlyList<int> Deltas, double CostBefore, double CostAfter);

/// <summary>
/// Colour stretches, found without bad-MCU detection. Working top to bottom, at every position every stretch from
/// <see cref="StretchMinLength"/> MCUs to <see cref="StretchMaxRowShare"/> of a row is tried: its colour offset is the
/// median, per component, of its MCUs' offsets against the MCU above (median of the sample differences along the
/// top edge). The stretch is scored by the seams along its top and its bottom edge, sample by sample, before and
/// after shifting it by that offset (Y + ½(Cb + Cr)). Checking the row below too keeps natural horizontal edges, which
/// differ from one side only, from being "corrected". A stretch qualifies if its first and its last MCU each improve,
/// and the whole stretch improves by at least <see cref="StretchMinGainShare"/> and <see cref="StretchMinGainPerMcu"/>
/// levels per MCU, with an offset of at least <see cref="StretchMinLevels"/>. Of the stretches that qualify, the one
/// with the biggest total gain is taken, the shorter one on a tie: an MCU that doesn't improve adds nothing, so a
/// stretch never wins by taking in MCUs that don't need the fix. Blank and missing MCUs are left out of the scoring and
/// keep their grey.
/// </summary>
public sealed partial class JpegDocument
{
    private const int StretchMinLength = 5;
    private const double StretchMaxRowShare = 0.5;
    private const float StretchMinGainShare = 0.5f;
    private const float StretchMinGainPerMcu = 1.5f;
    private const float StretchMinLevels = 4f;

    /// <summary>
    /// The first colour stretch (see the class remarks) from position <paramref name="from"/> on, or null. A pass goes on
    /// from <see cref="ColourStretchFix.Last"/> + 1 after applying each fix, so every stretch is judged against rows
    /// above it that are already fixed. Changes nothing; can run on a worker thread as long as the document is not
    /// edited meanwhile.
    /// </summary>
    public ColourStretchFix? FindNextColourStretch(int from, CancellationToken cancellation = default)
    {
        int perRow = Frame.McusPerRow;
        int count = Math.Min(_mcus.Count, Frame.McuCount);
        if (count <= perRow)
            return null;

        int maxLength = Math.Max(StretchMinLength, (int)(perRow * StretchMaxRowShare));
        var scan = new StretchScan(this, count);
        int start = Math.Max(from, perRow);
        for (int x = start; x < count; x++)
        {
            if ((x - start) % perRow == 0)
            {
                cancellation.ThrowIfCancellationRequested();
                Checkpoint();
            }

            if (scan.FixAt(x, maxLength) is { } fix)
                return fix;
        }

        return null;
    }

    /// <summary>Applies a fix from <see cref="FindNextColourStretch"/> as one undo step.</summary>
    public void ApplyColourStretch(ColourStretchFix fix)
    {
        if (fix.First < 0 || fix.Last >= _mcus.Count || fix.First > fix.Last)
            throw new ArgumentOutOfRangeException(nameof(fix));
        Apply(new RegionEdit(DescribeColourStretch(fix), [new RangeDcEdit(fix.First, fix.Last, fix.Deltas.ToArray())],
            fix.First, fix.Last));
    }

    public string DescribeColourStretch(ColourStretchFix fix) =>
        $"Fix colours of MCUs {fix.First}–{fix.Last} (DC {string.Join(", ", fix.Deltas.Select(d => d.ToString("+0;-0;0")))})";

    /// <summary>The seams of one MCU with the MCU above and the MCU below: sorted sample differences and their prefix sums, per component.</summary>
    private sealed class StretchSeam(float[] above, float[] abovePrefix, float[] medians, float[]? below, float[]? belowPrefix)
    {
        public float[] Above { get; } = above;

        public float[] AbovePrefix { get; } = abovePrefix;

        /// <summary>Median of the differences with the MCU above, per component (this MCU's own colour offset).</summary>
        public float[] Medians { get; } = medians;

        public float[]? Below { get; } = below;

        public float[]? BelowPrefix { get; } = belowPrefix;
    }

    /// <summary>Edge samples and seams of the positions one search looks at, read when first needed.</summary>
    private sealed class StretchScan
    {
        private readonly JpegDocument _doc;
        private readonly int _perRow, _count, _components, _rowLength;
        private readonly int[] _width, _top, _bottom;
        private readonly float[] _weights, _step, _buffer;
        private readonly float[]?[] _rows;
        private readonly bool[] _rowsRead;
        private readonly StretchSeam?[] _seams;
        private readonly bool[] _seamsDone;

        public StretchScan(JpegDocument doc, int count)
        {
            _doc = doc;
            _perRow = doc.Frame.McusPerRow;
            _count = count;
            var components = doc.Frame.Components;
            _components = components.Count;
            _width = new int[_components];
            _top = new int[_components];
            _bottom = new int[_components];
            _weights = new float[_components];
            _step = new float[_components];
            for (int c = 0; c < _components; c++)
            {
                _width[c] = components[c].H * 8;
                _top[c] = doc._planeOffset[c];
                _bottom[c] = doc._planeOffset[c] + (components[c].V * 8 - 1) * _width[c];
                _weights[c] = c == 0 ? 1f : 0.5f;
                _step[c] = doc._quant[c][0] / 8f;
                _rowLength += _width[c];
            }

            _buffer = new float[doc._samplesPerMcu];
            _rows = new float[]?[count];
            _rowsRead = new bool[count];
            _seams = new StretchSeam?[count];
            _seamsDone = new bool[count];
        }

        /// <summary>Top sample rows of all components, then bottom rows; null for blank and missing MCUs.</summary>
        private float[]? Rows(int i)
        {
            if (_rowsRead[i])
                return _rows[i];
            _rowsRead[i] = true;
            var entry = _doc._mcus[i];
            if (entry.Status is McuStatus.Blank or McuStatus.Missing)
                return null;

            _doc.EntrySamples(entry, _buffer);
            var rows = new float[2 * _rowLength];
            int o = 0;
            for (int c = 0; c < _components; c++)
            {
                for (int x = 0; x < _width[c]; x++)
                {
                    rows[o + x] = _buffer[_top[c] + x];
                    rows[_rowLength + o + x] = _buffer[_bottom[c] + x];
                }

                o += _width[c];
            }

            return _rows[i] = rows;
        }

        /// <summary>Sorted differences (top of <paramref name="lower"/> minus bottom of <paramref name="upper"/>) per component, with prefix sums.</summary>
        private (float[] Sorted, float[] Prefix) Differences(float[] lower, float[] upper)
        {
            var sorted = new float[_rowLength];
            var prefix = new float[_rowLength + _components];
            int o = 0;
            for (int c = 0; c < _components; c++)
            {
                int n = _width[c];
                for (int x = 0; x < n; x++)
                    sorted[o + x] = lower[o + x] - upper[_rowLength + o + x];
                Array.Sort(sorted, o, n);
                int p = o + c;
                prefix[p] = 0;
                for (int x = 0; x < n; x++)
                    prefix[p + x + 1] = prefix[p + x] + sorted[o + x];
                o += n;
            }

            return (sorted, prefix);
        }

        /// <summary>The seams of position <paramref name="k"/>; null if it or the MCU above has no samples.</summary>
        private StretchSeam? Seam(int k)
        {
            if (_seamsDone[k])
                return _seams[k];
            _seamsDone[k] = true;
            var self = Rows(k);
            var up = Rows(k - _perRow);
            if (self == null || up == null)
                return null;

            var (above, abovePrefix) = Differences(self, up);
            var medians = new float[_components];
            int o = 0;
            for (int c = 0; c < _components; c++)
            {
                int n = _width[c];
                medians[c] = 0.5f * (above[o + (n - 1) / 2] + above[o + n / 2]);
                o += n;
            }

            float[]? below = null, belowPrefix = null;
            if (k + _perRow < _count && Rows(k + _perRow) is { } down)
                (below, belowPrefix) = Differences(down, self);
            return _seams[k] = new StretchSeam(above, abovePrefix, medians, below, belowPrefix);
        }

        /// <summary>Mean |difference − <paramref name="delta"/>| of component <paramref name="c"/>.</summary>
        private float MeanAbs(float[] sorted, float[] prefix, int c, int o, float delta)
        {
            int n = _width[c], p = o + c;
            int lo = 0, hi = n;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[o + mid] < delta)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            float below = delta * lo - prefix[p + lo];
            float above = prefix[p + n] - prefix[p + lo] - delta * (n - lo);
            return (below + above) / n;
        }

        /// <summary>Seam cost of an MCU shifted by <paramref name="delta"/> (levels per component).</summary>
        private float Cost(StretchSeam seam, ReadOnlySpan<float> delta)
        {
            float sum = 0;
            int o = 0;
            for (int c = 0; c < _components; c++)
            {
                // Above: (top − delta) − bottom above. Below: top below − (bottom − delta).
                float cost = MeanAbs(seam.Above, seam.AbovePrefix, c, o, delta[c]);
                if (seam.Below != null)
                    cost += MeanAbs(seam.Below, seam.BelowPrefix!, c, o, -delta[c]);
                sum += _weights[c] * cost;
                o += _width[c];
            }

            return sum;
        }

        private float Size(ReadOnlySpan<float> delta)
        {
            float s = 0;
            for (int c = 0; c < _components; c++)
                s += _weights[c] * Math.Abs(delta[c]);
            return s;
        }

        /// <summary>The fix for the stretch starting at <paramref name="x"/> (see the class remarks), or null.</summary>
        public ColourStretchFix? FixAt(int x, int maxLength)
        {
            Span<float> zero = stackalloc float[_components];
            Span<float> delta = stackalloc float[_components];
            Span<float> best = stackalloc float[_components];
            var medians = new float[_components][];
            for (int c = 0; c < _components; c++)
                medians[c] = new float[maxLength];

            int n = 0, bestLength = 0, bestN = 0;
            float before = 0, bestBefore = 0, bestAfter = 0, bestGain = 0;
            var first = Seam(x);
            for (int length = 1; length <= maxLength && x + length - 1 < _count; length++)
            {
                var seam = Seam(x + length - 1);
                if (seam != null)
                {
                    for (int c = 0; c < _components; c++)
                    {
                        var m = medians[c];
                        float v = seam.Medians[c];
                        int i = n;
                        while (i > 0 && m[i - 1] > v)
                        {
                            m[i] = m[i - 1];
                            i--;
                        }

                        m[i] = v;
                    }

                    n++;
                    before += Cost(seam, zero);
                }

                if (length < StretchMinLength || n < StretchMinLength || first == null || seam == null)
                    continue;

                for (int c = 0; c < _components; c++)
                    delta[c] = 0.5f * (medians[c][(n - 1) / 2] + medians[c][n / 2]);
                if (Size(delta) < StretchMinLevels)
                    continue;
                if (Cost(first, delta) >= Cost(first, zero) || Cost(seam, delta) >= Cost(seam, zero))
                    continue;   // the first and the last MCU must each improve

                float after = 0;
                for (int j = 0; j < length; j++)
                {
                    if (Seam(x + j) is { } s)
                        after += Cost(s, delta);
                }

                float gain = before - after;
                if (gain < StretchMinGainShare * before || gain / n < StretchMinGainPerMcu || gain <= bestGain)
                    continue;

                bestGain = gain;
                bestLength = length;
                bestN = n;
                bestBefore = before;
                bestAfter = after;
                delta.CopyTo(best);
            }

            if (bestLength == 0)
                return null;

            var deltas = new int[_components];
            for (int c = 0; c < _components; c++)
                deltas[c] = (int)MathF.Round(-best[c] / _step[c]);
            if (deltas.All(d => d == 0))
                return null;

            return new ColourStretchFix(x, x + bestLength - 1, deltas, bestBefore / bestN, bestAfter / bestN);
        }
    }

    /// <summary>Adds <c>deltas</c> to the DC of every block of the MCUs at positions first..last (blanks are left alone).</summary>
    private sealed class RangeDcEdit(int first, int last, int[] deltas) : Edit(first)
    {
        public override int LastMcu => last;

        public override string Description => $"Adjust DC of MCUs {first}–{last}";

        public override void Apply(JpegDocument document) => Shift(document, +1);

        public override void Revert(JpegDocument document) => Shift(document, -1);

        private void Shift(JpegDocument document, int sign)
        {
            var blocks = document.Layout.Blocks;
            for (int i = first; i <= Math.Min(last, document._mcus.Count - 1); i++)
            {
                var entry = document._mcus[i];
                if (entry.IsBlank)
                    continue;
                for (int b = 0; b < blocks.Count; b++)
                {
                    int delta = deltas[blocks[b].ComponentIndex];
                    if (delta != 0)
                        entry.Coefficients[b * 64] = (short)(entry.Coefficients[b * 64] + sign * delta);
                }
            }
        }
    }

    /// <summary>Several edits, undone and redone together, that only change positions first..last.</summary>
    private sealed class RegionEdit(string description, Edit[] parts, int first, int last) : Edit(first)
    {
        public override int LastMcu => last;

        public override string Description => description;

        public override void Apply(JpegDocument document)
        {
            foreach (var part in parts)
                part.Apply(document);
        }

        public override void Revert(JpegDocument document)
        {
            for (int i = parts.Length - 1; i >= 0; i--)
                parts[i].Revert(document);
        }
    }
}
