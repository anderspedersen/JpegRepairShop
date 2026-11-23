using System.Collections.Concurrent;

namespace JpegRepair.Core;

/// <param name="Shift">MCUs to insert (positive) or remove (negative).</param>
/// <param name="Fit">Share of neighbour pixels that agree (0..1) with this shift.</param>
public sealed record AlignmentCandidate(int Shift, double Fit);

/// <param name="Shift">Best shift: MCUs to insert (positive) or remove (negative); 0 = already aligned.</param>
/// <param name="Fit">Fit of the best shift.</param>
/// <param name="FitWithoutShift">Fit as things are now, for comparison.</param>
/// <param name="Candidates">Every shift that was tried, with its fit.</param>
/// <param name="CountPenalty">
/// Cost of the shift for moving the MCU count further from the number the picture needs (0 if it moves it closer).
/// </param>
public sealed record AlignmentEstimate(int Shift, double Fit, double FitWithoutShift, IReadOnlyList<AlignmentCandidate> Candidates, double CountPenalty = 0)
{
    /// <summary>
    /// How much the shift improves the fit, minus the count penalty. The line-start bonus helps choose the shift but
    /// is not counted here (with one row of seams it is too noisy to decide whether a place is damaged).
    /// </summary>
    public double Improvement => Fit - FitWithoutShift - CountPenalty;
}

/// <param name="Index">The MCU where the damage starts: insert or remove MCUs and/or fix the colours from here.</param>
/// <param name="Estimate">The shift to apply there, with its fit; null if only the colours are wrong.</param>
/// <param name="ColourFix">
/// DC change per component (all zero if the colours already match). For a colour shift without misalignment it is
/// known when found; after aligning it is what was applied. Null if not known yet.
/// </param>
/// <param name="FromLineStarts">True if the shift was found from where lines start (the line-start track), not from the fit.</param>
public sealed record MisalignmentResult(int Index, AlignmentEstimate? Estimate, IReadOnlyList<int>? ColourFix = null, bool FromLineStarts = false)
{
    /// <summary>MCUs to insert (positive) or remove (negative); 0 = only the colours need fixing.</summary>
    public int Shift => Estimate?.Shift ?? 0;
}

/// <summary>
/// Alignment: finding how many MCUs were lost (or added) at a place, and finding the place.
///
/// For a place and a candidate shift, the differences between the bottom pixel row of the MCU row above and the
/// top pixel row of the (shifted) MCUs are put into buckets; the share of pixels in the biggest bucket is the fit.
/// A colour shift moves all differences by the same amount, so a correct alignment still gives one big bucket,
/// while a wrong alignment spreads the differences out. Brightness counts fully and colour (Cb, Cr) at half weight;
/// buckets are 8 levels wide (or one DC step, if wider).
///
/// Close calls are settled by where line starts land: neighbouring MCUs in the stream normally
/// join smoothly from left to right, except where the second one is really the first MCU of the next line. So every
/// left-right seam is scored (how badly the right edge of one MCU meets the left edge of the next), and a shift gets a
/// small bonus (at most <see cref="SeamWeight"/>) if it puts the unusually bad seams of the next <see cref="SeamRows"/>
/// row at column 0. The bonus only chooses the shift; whether a place counts as damaged depends on the fit alone.
/// </summary>
public sealed partial class JpegDocument
{
    private const int MinBucketWidth = 8;
    private const double MinImprovement = 0.05;

    /// <summary>Minimum score (share of neighbour pixels that agree better) for a colour shift without misalignment.</summary>
    private const double MinColourScore = 0.05;

    /// <summary>Colour shifts smaller than this (in sample levels, 0..255) are left alone: close enough.</summary>
    private const float MinColourShiftLevels = 4f;
    private const int HistogramOffset = 512;
    private const int HistogramSize = 1024;

    /// <summary>Weight of the line-start (seam) score next to the fit: enough to settle close calls, never more than this.</summary>
    private const double SeamWeight = 0.05;

    /// <summary>
    /// Cost of a shift per half row of MCUs that it moves the MCU count further from the frame's MCU count. Lost data
    /// leaves too few MCUs (the picture ends early), garbage too many (overflow after the picture), so a shift away
    /// from the right count is suspicious. Moving towards it is free.
    /// </summary>
    private const double CountPenaltyPerHalfRow = 0.1;

    /// <summary>Cost of changing the line-start column between two MCU rows in the line-start track.</summary>
    private const double LineStartChangeCost = 80;

    /// <summary>
    /// A shift found from line starts alone (the fit does not agree) is used only if it makes the fit at most this
    /// much worse: it may settle a place where the fit cannot tell (garbage above), not overrule a clear fit.
    /// </summary>
    private const double LineStartFitTolerance = 0.05;

    /// <summary>
    /// MCU rows after the place whose seams are used for the line-start score. More rows would pick up the line starts of
    /// the next damage when it follows close by.
    /// </summary>
    private const int SeamRows = 1;

    /// <summary>
    /// Finds out how many MCUs to insert (positive shift) or remove (negative) at <paramref name="index"/> so the
    /// MCUs from there on line up with the row above. Every shift up to half a row in either direction is simulated
    /// without changing anything. Returns null if there is no row above or too little intact data to compare.
    /// </summary>
    public AlignmentEstimate? EstimateAlignment(int index)
    {
        int perRow = Frame.McusPerRow;
        if (index < perRow || index >= Math.Min(_mcus.Count, Frame.McuCount))
            return null;

        var rows = new BoundaryRows(this, index - perRow, index + SeamRows * perRow + perRow / 2 + 1);
        return new AlignmentScorer(this, rows).Estimate(index, keepCandidates: true);
    }

    /// <summary>Inserts or removes MCUs at <paramref name="index"/> as found by <see cref="EstimateAlignment"/> (one undo step).</summary>
    public AlignmentEstimate? AutoAlign(int index)
    {
        var estimate = EstimateAlignment(index);
        if (estimate != null)
            ApplyShift(index, estimate.Shift);
        return estimate;
    }

    /// <summary>
    /// Searches the whole picture for the place where aligning helps most: the alignment estimate is run every
    /// quarter row, then at every MCU around the best place, and finally the exact MCU where the shift starts is found
    /// by checking, MCU by MCU, whether it matches the row above better unshifted or shifted.
    /// Before that, the line-start track is checked (see <see cref="FindLineStartShift"/>): it also finds shifts below
    /// a band of garbage, where the row above is useless for the fit.
    /// If nothing is misaligned, looks for a colour shift on its own instead (see <see cref="FindColourShift"/>).
    /// Returns null if no place improves noticeably.
    /// </summary>
    public MisalignmentResult? FindMisalignment()
    {
        int perRow = Frame.McusPerRow;
        int count = Math.Min(_mcus.Count, Frame.McuCount);
        if (count <= perRow)
            return null;

        Checkpoint();
        var rows = new BoundaryRows(this, 0, _mcus.Count);
        Checkpoint();
        if (FindLineStartShift(rows) is { } fromLineStarts)
            return fromLineStarts;

        int step = Math.Max(1, perRow / 4);

        // Sample every step-th MCU of the original decode, not every step-th position: after an insert or remove the
        // same MCUs are sampled again, so their cached fits can be reused (see AlignmentScorer.Fits).
        var coarse = new List<int>();
        for (int i = perRow; i < count; i++)
        {
            var entry = _mcus[i];
            if (!entry.IsInserted && entry.SourceIndex % step == 0)
                coarse.Add(i);
        }
        var best = BestImprovement(rows, coarse);
        if (best == null || best.Value.Estimate.Improvement < MinImprovement)
            return FindColourShift(rows);

        var fine = new List<int>();
        for (int i = Math.Max(perRow, best.Value.Index - step); i <= Math.Min(count - 1, best.Value.Index + step); i++)
            fine.Add(i);
        best = BestImprovement(rows, fine) ?? best;

        var (index, estimate) = best.Value;
        int start = FindShiftStart(rows, index, estimate.Shift);
        return new MisalignmentResult(start, estimate);
    }

    /// <summary>
    /// Finds the biggest misalignment (see <see cref="FindMisalignment"/>), fixes it there and then fixes the colour
    /// shift that usually comes with it (one undo step).
    /// </summary>
    public MisalignmentResult? FindAndAlign()
    {
        var result = FindMisalignment();
        return result == null ? null : result with { ColourFix = AlignAndFixColors(result.Index, result.Shift) };
    }

    /// <summary>
    /// Inserts (<paramref name="shift"/> &gt; 0) or removes (&lt; 0) MCUs at <paramref name="index"/>, then corrects the
    /// colour shift of the MCUs that now follow the row above them properly (as <see cref="AutoFixColors"/>).
    /// Both are one undo step. Returns the DC change per component (all zero if the colours already matched).
    /// </summary>
    public int[] AlignAndFixColors(int index, int shift)
    {
        if (index < 0 || index >= _mcus.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var parts = new List<Edit>(2);
        string description = "Fix colour shift";
        if (shift != 0)
        {
            var shiftEdit = shift > 0 ? InsertBlankEdit(index, shift) : new RemoveEdit(index, Math.Min(-shift, _mcus.Count - index));
            shiftEdit.Apply(this);
            parts.Add(shiftEdit);
            description = shiftEdit.Description;

            // Judge bad MCUs as they are after the shift: MCUs that only looked bad because they were out of place
            // must take part in the colour estimate. Redraws the rows from here (again when the edit is recorded).
            RenderFrom(index);
        }

        // After inserting, the first MCU that was moved is after the blanks; the DC edit skips blanks anyway.
        int colourIndex = Math.Min(index + Math.Max(shift, 0), _mcus.Count - 1);
        var deltas = EstimateDcShift(colourIndex, ColourFixSkips());
        if (deltas.Any(d => d != 0))
        {
            var dcEdit = new DcEdit(colourIndex, deltas, "Fix colour shift");
            dcEdit.Apply(this);
            parts.Add(dcEdit);
            description = parts.Count > 1 ? description + " and fix colours" : dcEdit.Description;
        }

        if (parts.Count == 1)
            Record(parts[0]);
        else if (parts.Count > 1)
            Record(new CompositeEdit(description, parts.ToArray()));
        return deltas;
    }

    private void ApplyShift(int index, int shift)
    {
        if (shift > 0)
            InsertBlank(index, shift);
        else if (shift < 0)
            Remove(index, -shift);
    }

    private (int Index, AlignmentEstimate Estimate)? BestImprovement(BoundaryRows rows, List<int> positions)
    {
        (int Index, AlignmentEstimate Estimate)? best = null;
        var gate = new object();
        Parallel.For(0, positions.Count, () => new AlignmentScorer(this, rows), (k, _, scorer) =>
        {
            Checkpoint();
            var estimate = scorer.Estimate(positions[k], keepCandidates: false);
            if (estimate is { Shift: not 0 })
            {
                lock (gate)
                {
                    if (best == null
                        || estimate.Improvement > best.Value.Estimate.Improvement
                        || (estimate.Improvement == best.Value.Estimate.Improvement && positions[k] < best.Value.Index))
                    {
                        best = (positions[k], estimate);
                    }
                }
            }

            return scorer;
        }, _ => { });
        return best;
    }

    /// <summary>
    /// Near <paramref name="index"/>, finds the split point where MCUs before it match the row above best unshifted
    /// and MCUs after it match best with <paramref name="shift"/> (brightness only).
    /// </summary>
    private int FindShiftStart(BoundaryRows rows, int index, int shift)
    {
        int perRow = Frame.McusPerRow;
        int count = Math.Min(_mcus.Count, Frame.McuCount);
        int lo = Math.Max(perRow, index - perRow / 4);
        int hi = Math.Min(count, index + perRow / 4 + perRow / 2);
        int width = BucketWidth(0);
        var histogram = new int[HistogramSize];

        // The usual difference across the boundary before the split (unshifted) and after it (shifted).
        int Offset(int from, int to, int s)
        {
            Array.Clear(histogram);
            int total = 0;
            for (int m = from; m < to; m++)
                total += AddDifferences(rows, m, m + s, count, histogram);
            return total == 0 ? 0 : BiggestBucketStart(histogram, width);
        }

        int unshiftedOffset = Offset(Math.Max(perRow, index - perRow / 2), index, 0);
        int shiftedOffset = Offset(index, Math.Min(count, index + perRow / 2), shift);

        double Agreement(int source, int position, int offset)
        {
            var d = LumaDifferences(rows, source, position, count);
            if (d == null)
                return 0.5;
            int inside = 0;
            foreach (float v in d)
            {
                if (v >= offset && v <= offset + width)
                    inside++;
            }

            return (double)inside / d.Length;
        }

        int n = hi - lo;
        if (n <= 0)
            return index;
        var unshifted = new double[n];
        var shifted = new double[n];
        for (int m = lo; m < hi; m++)
        {
            unshifted[m - lo] = Agreement(m, m, unshiftedOffset);
            shifted[m - lo] = Agreement(m, m + shift, shiftedOffset);
        }

        int removed = Math.Max(0, -shift);
        int bestSplit = index;
        double bestScore = double.MinValue;
        for (int q = lo; q <= Math.Min(hi, index + perRow / 4); q++)
        {
            Checkpoint();
            double score = 0.5 * removed;
            for (int m = lo; m < q; m++)
                score += unshifted[m - lo];
            for (int m = q + removed; m < hi; m++)
                score += shifted[m - lo];
            if (score > bestScore)
            {
                bestScore = score;
                bestSplit = q;
            }
        }

        return bestSplit;
    }

    /// <summary>
    /// Finds the biggest colour shift where nothing is misaligned: from the MCU where it starts, one row's worth of MCUs
    /// differs from the row above by the same amount, while the MCUs before it (and the next row) match.
    /// <list type="number">
    /// <item>Every eighth of a row, one row's worth of boundary differences is bucketed; a place is a candidate if the
    /// biggest bucket holds clearly more than the bucket around 0 (no change).</item>
    /// <item>For the best candidates, every MCU gets a gain (agreement with the shifted bucket minus agreement with 0),
    /// and the start is where one row's worth of gains adds up to the most.</item>
    /// <item>The score is the smaller of the average gain on the rest of the start row and on the start of the next
    /// row, minus any gain before the start. A natural horizontal edge exactly on an MCU boundary gains on one row
    /// only, so it scores nothing (which also means a shift starting in the first few columns is not found).</item>
    /// <item>The fix is <see cref="EstimateDcShift"/> at the start; shifts under 4 levels are left alone.</item>
    /// </list>
    /// </summary>
    private MisalignmentResult? FindColourShift(BoundaryRows rows)
    {
        int perRow = Frame.McusPerRow;
        int count = Math.Min(_mcus.Count, Frame.McuCount);
        int components = Frame.Components.Count;
        int step = Math.Max(1, perRow / 8);
        if (count <= perRow)
            return null;

        // 1. Candidate places.
        var positions = new List<int>();
        for (int i = perRow; i < count; i += step)
            positions.Add(i);
        var found = new (double Improvement, int[]? Offsets)[positions.Count];
        Parallel.For(0, positions.Count, () => new ColourWindow(this, rows), (k, _, window) =>
        {
            Checkpoint();
            found[k] = window.Improvement(positions[k]);
            return window;
        }, _ => { });

        var ranked = Enumerable.Range(0, positions.Count)
            .Where(k => found[k].Offsets != null && found[k].Improvement >= 0.03)
            .OrderByDescending(k => found[k].Improvement)
            .ThenBy(k => k)
            .ToList();
        var picked = new List<int>();
        foreach (int k in ranked)
        {
            if (picked.All(p => Math.Abs(positions[p] - positions[k]) >= perRow / 2))
                picked.Add(k);
            if (picked.Count >= 10)
                break;
        }

        // 2.-4. Exact start, score and fix for each candidate.
        MisalignmentResult? best = null;
        double bestScore = 0;
        foreach (int k in picked)
        {
            Checkpoint();
            int i = positions[k];
            var offsets = found[k].Offsets!;
            int from = Math.Max(perRow, i - perRow), to = Math.Min(count, i + 2 * perRow);
            var gains = new double?[to - from];
            for (int p = from; p < to; p++)
                gains[p - from] = ColourGain(rows, p, offsets);
            double Gain(int p) => p >= from && p < to ? gains[p - from] ?? 0 : 0;

            int start = -1;
            double bestSum = double.MinValue;
            for (int q = Math.Max(perRow, i - perRow / 2); q <= Math.Min(count - 1, i + perRow / 2); q++)
            {
                double sum = 0;
                for (int p = q; p < q + perRow; p++)
                    sum += Gain(p);
                if (sum > bestSum)
                {
                    bestSum = sum;
                    start = q;
                }
            }

            if (start < 0)
                continue;
            double? score = SplitScore(start, p => p >= from && p < to ? gains[p - from] : null);
            if (score == null || score < MinColourScore || (best != null && score <= bestScore))
                continue;

            var deltas = EstimateDcShift(start);
            float levels = 0;
            for (int c = 0; c < components; c++)
                levels = Math.Max(levels, Math.Abs(deltas[c]) * _quant[c][0] / 8f);
            if (levels < MinColourShiftLevels)
                continue;

            best = new MisalignmentResult(start, null, deltas);
            bestScore = score.Value;
        }

        return best;
    }

    /// <summary>
    /// The smaller of the average gain on the rest of the start row and on the next row up to one row's worth of MCUs,
    /// minus any average gain before the start; null if either part has fewer than 3 MCUs to compare.
    /// </summary>
    private double? SplitScore(int start, Func<int, double?> gain)
    {
        int perRow = Frame.McusPerRow;
        int rowStart = start / perRow * perRow;

        static (double Sum, int Count) Add(int from, int to, Func<int, double?> gain)
        {
            double sum = 0;
            int n = 0;
            for (int p = from; p < to; p++)
            {
                if (gain(p) is { } g)
                {
                    sum += g;
                    n++;
                }
            }

            return (sum, n);
        }

        var rest = Add(start, rowStart + perRow, gain);
        var next = Add(rowStart + perRow, start + perRow, gain);
        var before = Add(rowStart, start, gain);
        if (rest.Count < 3 || next.Count < 3)
            return null;

        double score = Math.Min(rest.Sum / rest.Count, next.Sum / next.Count);
        if (before.Count > 0)
            score -= Math.Max(0, before.Sum / before.Count);
        return score;
    }

    /// <summary>
    /// For MCU <paramref name="position"/> (unshifted): share of boundary differences in the bucket starting at
    /// <paramref name="offsets"/> minus share in the bucket around 0, combined over the components like the fit; null if not comparable.
    /// </summary>
    private double? ColourGain(BoundaryRows rows, int position, int[] offsets)
    {
        int perRow = Frame.McusPerRow;
        var above = rows.Bottom(position - perRow);
        var current = rows.Top(position);
        if (above == null || current == null)
            return null;

        int components = Frame.Components.Count;
        Span<double> gain = stackalloc double[Math.Max(3, components)];
        for (int c = 0; c < components; c++)
        {
            int width = BucketWidth(c);
            int zero = -(width / 2);
            int columns = VisibleColumns(position, c);
            int offset = rows.RowOffsets[c];
            int shifted = 0, unchanged = 0;
            for (int x = 0; x < columns; x++)
            {
                int d = Bin(above[offset + x] - current[offset + x]) - HistogramOffset;
                if (d >= offsets[c] && d <= offsets[c] + width)
                    shifted++;
                if (d >= zero && d <= zero + width)
                    unchanged++;
            }

            gain[c] = columns == 0 ? 0 : (double)(shifted - unchanged) / columns;
        }

        return components >= 3 ? (gain[0] + 0.5 * (gain[1] + gain[2]) / 2) / 1.5 : gain[0];
    }

    /// <summary>MCUs that hold decoded data or blanks: all entries except Missing ones at the end (data ran out).</summary>
    private int DataMcuCount()
    {
        int n = _mcus.Count;
        while (n > 0 && _mcus[n - 1].Status == McuStatus.Missing)
            n--;
        return n;
    }

    /// <summary>
    /// In the stream, the MCU that starts a line usually joins badly to the one before it, so in every MCU row of the
    /// stream the unusually bad seams vote for the column where lines really start .
    /// </summary>
    private MisalignmentResult? FindLineStartShift(BoundaryRows rows)
    {
        int perRow = Frame.McusPerRow;
        int count = Math.Min(_mcus.Count, Frame.McuCount);
        int rowCount = count / perRow;
        if (rowCount < 2 || perRow < 4)
            return null;

        // Votes per stream row.
        var votes = new double[rowCount][];
        var usable = new bool[rowCount];
        Parallel.For(0, rowCount, k =>
        {
            Checkpoint();
            var z = new double[perRow];
            var values = new List<double>(perRow);
            for (int x = 0; x < perRow; x++)
            {
                if (rows.Seam(k * perRow + x) is { } h)
                    values.Add(h);
            }

            votes[k] = z;
            if (values.Count == 0 || values.Count < perRow / 2)
                return;

            double median = MedianOf(values.ToArray());
            double spread = MedianOf(values.Select(v => Math.Abs(v - median)).ToArray()) + 1e-3;
            for (int x = 0; x < perRow; x++)
            {
                if (rows.Seam(k * perRow + x) is { } h)
                    z[x] = Math.Clamp((h - median) / spread, -5, 20);
            }

            usable[k] = true;
        });

        // Best path.
        var score = new double[perRow];
        Array.Fill(score, -1e18);
        score[0] = 0;
        var back = new int[rowCount][];
        for (int k = 0; k < rowCount; k++)
        {
            int best = ArgMax(score);
            double switched = score[best] - LineStartChangeCost;
            back[k] = new int[perRow];
            var next = new double[perRow];
            for (int r = 0; r < perRow; r++)
            {
                bool change = switched > score[r];
                back[k][r] = change ? best : r;
                next[r] = (change ? switched : score[r]) + (usable[k] ? votes[k][r] : 0);
            }

            score = next;
        }

        var path = new int[rowCount];
        path[rowCount - 1] = ArgMax(score);
        for (int k = rowCount - 1; k > 0; k--)
            path[k - 1] = back[k][path[k]];

        // Changes, first to last.
        int previous = 0;
        AlignmentScorer? scorer = null;
        for (int k = 0; k < rowCount; k++)
        {
            if (path[k] == previous)
                continue;

            int before = previous, after = path[k];
            previous = path[k];

            int last = k - 1;
            for (int kk = k - 1; kk >= 0; kk--)
            {
                if (usable[kk] && ArgMax(votes[kk]) == before)
                {
                    last = kk;
                    break;
                }
            }

            int first = k;
            for (int kk = k; kk < rowCount; kk++)
            {
                if (path[kk] == after && usable[kk] && ArgMax(votes[kk]) == after)
                {
                    first = kk;
                    break;
                }
            }

            int shift = ((before - after) % perRow + perRow) % perRow;
            if (shift > perRow / 2)
                shift -= perRow;

            // Where the new content starts: after the worst seam in the line before its first clear line start.
            int newLineStart = first * perRow + after;
            int lo = Math.Max(last * perRow + before + 1, newLineStart - perRow + 1), hi = newLineStart;
            int contentStart = lo;
            float worst = float.MinValue;
            for (int p = lo; p < hi; p++)
            {
                if (rows.Seam(p) is { } h && h > worst)
                {
                    worst = h;
                    contentStart = p;
                }
            }

            scorer ??= new AlignmentScorer(this, rows);
            if (PlaceLineStartShift(rows, scorer, shift, k, contentStart) is { } result)
                return result;
        }

        return null;
    }

    private MisalignmentResult? PlaceLineStartShift(BoundaryRows rows, AlignmentScorer scorer, int shift, int row, int contentStart)
    {
        int perRow = Frame.McusPerRow;
        int count = Math.Min(_mcus.Count, Frame.McuCount);

        // Does the fit agree on this shift somewhere in the rows around the change?
        int lo = Math.Max(perRow, (row - 3) * perRow), hi = Math.Min(count - 1, (row + 2) * perRow);
        int step = Math.Max(1, perRow / 8);
        int agreeIndex = -1;
        AlignmentEstimate? agree = null;

        void Consider(int i)
        {
            var estimate = scorer.Estimate(i, keepCandidates: false);
            if (estimate != null && estimate.Shift == shift && estimate.Improvement >= MinImprovement
                && (agree == null || estimate.Improvement > agree.Improvement))
            {
                agree = estimate;
                agreeIndex = i;
            }
        }

        for (int i = lo; i < hi; i += step)
        {
            Checkpoint();
            Consider(i);
        }
        if (agree != null)
        {
            int center = agreeIndex;
            for (int i = Math.Max(perRow, center - step); i <= Math.Min(hi, center + step); i++)
            {
                Checkpoint();
                Consider(i);
            }
            return new MisalignmentResult(FindShiftStart(rows, agreeIndex, shift), agree);
        }

        // The fit does not agree: use the line starts if the fit cannot tell otherwise.
        if (contentStart < perRow)
            return null;
        var fit = scorer.FitAt(contentStart, shift);
        var fitWithout = scorer.FitAt(contentStart, 0);
        double penalty = scorer.Penalty(shift);
        if (fit == null || fitWithout == null || fit.Value - penalty < fitWithout.Value - LineStartFitTolerance)
            return null;

        int index = Math.Max(0, contentStart + Math.Min(shift, 0));
        var found = new AlignmentEstimate(shift, fit.Value, fitWithout.Value, Array.Empty<AlignmentCandidate>(), penalty);
        return new MisalignmentResult(index, found, FromLineStarts: true);
    }

    private static int ArgMax(double[] values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best])
                best = i;
        }

        return best;
    }

    private static double MedianOf(double[] values)
    {
        Array.Sort(values);
        int n = values.Length;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }

    private const int MaxCachedPlaces = 50_000;

    private readonly ConcurrentDictionary<McuEntry, CachedFits> _fitCache = new(ReferenceEqualityComparer.Instance);

    private EdgeLayout? _edgeLayout;

    private EdgeLayout Edges => _edgeLayout ??= new EdgeLayout(this);

    /// <summary>The fits of all shifts at one place, with what they were computed from.</summary>
    private sealed class CachedFits
    {
        private McuEntry[] _entries = [];
        private short[] _dc = [];

        public double?[] Fits { get; private init; } = [];

        public static CachedFits Capture(JpegDocument document, int lo, int hi, double?[] fits)
        {
            var first = document.Edges.FirstBlock;
            var entries = new McuEntry[hi - lo];
            var dc = new short[entries.Length * first.Length];
            for (int k = 0; k < entries.Length; k++)
            {
                var entry = entries[k] = document._mcus[lo + k];
                for (int c = 0; c < first.Length; c++)
                    dc[k * first.Length + c] = entry.Coefficients[first[c] * 64];
            }

            return new CachedFits { _entries = entries, _dc = dc, Fits = fits };
        }

        /// <summary>
        /// True if the MCUs from <paramref name="lo"/> are the same ones, and the DC of every compared (intact) MCU has
        /// changed by the same amount per component since (edits only change DC, and shift all MCUs after a place alike).
        /// </summary>
        public bool Matches(JpegDocument document, int lo)
        {
            var mcus = document._mcus;
            if (lo + _entries.Length > mcus.Count)
                return false;

            var first = document.Edges.FirstBlock;
            Span<int> delta = stackalloc int[first.Length];
            bool seen = false;
            for (int k = 0; k < _entries.Length; k++)
            {
                var entry = mcus[lo + k];
                if (!ReferenceEquals(entry, _entries[k]))
                    return false;
                if (entry.Status != McuStatus.Ok)
                    continue;
                for (int c = 0; c < first.Length; c++)
                {
                    int d = entry.Coefficients[first[c] * 64] - _dc[k * first.Length + c];
                    if (!seen)
                        delta[c] = d;
                    else if (d != delta[c])
                        return false;
                }

                seen = true;
            }

            return true;
        }
    }

    /// <summary>
    /// Where the edge samples of an MCU are kept in <see cref="McuEntry.AcEdges"/>, and which block each edge sample
    /// belongs to (to add the DC terms). Layout: top rows of all components, bottom rows, then per component the columns
    /// left, second from left, right, second from right, last visible, second-to-last visible (each V×8 samples).
    /// </summary>
    private sealed class EdgeLayout
    {
        public EdgeLayout(JpegDocument document)
        {
            var components = document.Frame.Components;
            int count = components.Count;
            Width = new int[count];
            Height = new int[count];
            LastVisible = new int[count];
            ColumnOffsets = new int[count];
            BlockAt = new int[count][,];
            FirstBlock = new int[count];
            int rowLength = 0, columnLength = 0;
            for (int c = 0; c < count; c++)
            {
                Width[c] = components[c].H * 8;
                Height[c] = components[c].V * 8;
                rowLength += Width[c];
                ColumnOffsets[c] = columnLength;
                columnLength += 6 * Height[c];
                BlockAt[c] = new int[components[c].V, components[c].H];
                FirstBlock[c] = -1;

                // Columns of the last MCU of a row that are inside the picture (fewer than all if the width is not a whole
                // number of MCUs); fewer than 2 can't be extrapolated, so then the full width is used.
                int visible = document.VisibleColumns(document.Frame.McusPerRow - 1, c);
                LastVisible[c] = visible < 2 ? Width[c] : visible;
            }

            for (int b = 0; b < document.Layout.BlocksPerMcu; b++)
            {
                var block = document.Layout.Blocks[b];
                BlockAt[block.ComponentIndex][block.BlockY, block.BlockX] = b;
                if (FirstBlock[block.ComponentIndex] < 0)
                    FirstBlock[block.ComponentIndex] = b;
            }

            TopOffset = 0;
            BottomOffset = rowLength;
            ColumnsOffset = 2 * rowLength;
            Length = 2 * rowLength + columnLength;
        }

        public int[] Width { get; }

        public int[] Height { get; }

        public int[] LastVisible { get; }

        public int[] ColumnOffsets { get; }

        /// <summary>Block index per component, [block row, block column].</summary>
        public int[][,] BlockAt { get; }

        public int[] FirstBlock { get; }

        public int TopOffset { get; }

        public int BottomOffset { get; }

        public int ColumnsOffset { get; }

        public int Length { get; }
    }

    /// <summary>The MCU's edge samples without DC, computed once per MCU (see <see cref="McuEntry.AcEdges"/>).</summary>
    private float[] AcEdgesOf(McuEntry entry, float[] samples)
    {
        if (entry.AcEdges is { } cached)
            return cached;

        var layout = Edges;
        var coefficients = new short[64];
        for (int b = 0; b < Layout.BlocksPerMcu; b++)
        {
            int component = Layout.Blocks[b].ComponentIndex;
            entry.Coefficients.AsSpan(b * 64, 64).CopyTo(coefficients);
            coefficients[0] = 0;
            Idct.TransformToFloat(coefficients, _quant[component], samples.AsSpan(_blockPlaneOffset[b]), Frame.Components[component].H * 8);
        }

        var edges = new float[layout.Length];
        int rowOffset = 0;
        for (int c = 0; c < Frame.Components.Count; c++)
        {
            int width = layout.Width[c], height = layout.Height[c];
            int plane = _planeOffset[c];
            Array.Copy(samples, plane, edges, layout.TopOffset + rowOffset, width);
            Array.Copy(samples, plane + (height - 1) * width, edges, layout.BottomOffset + rowOffset, width);
            rowOffset += width;

            int o = layout.ColumnsOffset + layout.ColumnOffsets[c];
            int visible = layout.LastVisible[c];
            for (int y = 0; y < height; y++)
            {
                int row = plane + y * width;
                edges[o + y] = samples[row];
                edges[o + height + y] = samples[row + 1];
                edges[o + 2 * height + y] = samples[row + width - 1];
                edges[o + 3 * height + y] = samples[row + width - 2];
                edges[o + 4 * height + y] = samples[row + visible - 1];
                edges[o + 5 * height + y] = samples[row + visible - 2];
            }
        }

        entry.AcEdges = edges;   // a race only computes the same thing twice
        return edges;
    }

    private int BucketWidth(int component) => (int)MathF.Ceiling(Math.Max(MinBucketWidth, _quant[component][0] / 8f));

    /// <summary>Luma differences (row above minus MCU) for MCU <paramref name="source"/> shown at <paramref name="position"/>, or null.</summary>
    private float[]? LumaDifferences(BoundaryRows rows, int source, int position, int count)
    {
        int perRow = Frame.McusPerRow;
        if (source < 0 || source >= _mcus.Count || position - perRow < 0 || position >= count)
            return null;
        var above = rows.Bottom(position - perRow);
        var current = rows.Top(source);
        if (above == null || current == null)
            return null;

        int columns = VisibleColumns(position, 0);
        var d = new float[columns];
        for (int x = 0; x < columns; x++)
            d[x] = above[x] - current[x];
        return d;
    }

    private int AddDifferences(BoundaryRows rows, int source, int position, int count, int[] histogram)
    {
        var d = LumaDifferences(rows, source, position, count);
        if (d == null)
            return 0;
        foreach (float v in d)
            histogram[Bin(v)]++;
        return d.Length;
    }

    private int VisibleColumns(int position, int component)
    {
        var frame = Frame;
        int visibleWidth = Math.Min(frame.McuWidth, frame.Width - position % frame.McusPerRow * frame.McuWidth);
        return (visibleWidth * frame.Components[component].H + frame.MaxH - 1) / frame.MaxH;
    }

    private static int Bin(float difference) => Math.Clamp((int)MathF.Round(difference) + HistogramOffset, 0, HistogramSize - 1);

    /// <summary>Start (as a difference value) of the fullest window of <paramref name="width"/> levels.</summary>
    private static int BiggestBucketStart(int[] histogram, int width)
    {
        int sum = 0, best = -1, bestStart = 0;
        for (int i = 0; i < HistogramSize; i++)
        {
            sum += histogram[i];
            if (i > width)
                sum -= histogram[i - width - 1];
            if (i >= width && sum > best)
            {
                best = sum;
                bestStart = i - width;
            }
        }

        return bestStart - HistogramOffset;
    }

    /// <summary>Top and bottom pixel rows of every intact MCU in a range, computed once (in parallel).</summary>
    private sealed class BoundaryRows
    {
        private readonly int _from;
        private readonly float[]?[] _top;
        private readonly float[]?[] _bottom;
        private readonly float[]?[] _columns;
        private readonly float[] _seam;

        public BoundaryRows(JpegDocument document, int from, int to)
        {
            from = Math.Max(0, from);
            to = Math.Min(document._mcus.Count, to);
            _from = from;
            int length = Math.Max(0, to - from);
            _top = new float[]?[length];
            _bottom = new float[]?[length];
            _columns = new float[]?[length];
            _seam = new float[length];

            var components = document.Frame.Components;
            RowOffsets = new int[components.Count];
            int rowLength = 0;
            for (int c = 0; c < components.Count; c++)
            {
                RowOffsets[c] = rowLength;
                rowLength += components[c].H * 8;
            }

            // Edge columns per component: left, second from left, right, second from right (each V*8 samples).
            var columnOffsets = new int[components.Count];
            int columnLength = 0;
            for (int c = 0; c < components.Count; c++)
            {
                columnOffsets[c] = columnLength;
                columnLength += 4 * components[c].V * 8;
            }

            var layout = document.Edges;
            int blocks = document.Layout.BlocksPerMcu;
            Parallel.For(0, length, () => (Samples: new float[document._samplesPerMcu], Dc: new float[blocks]), (k, _, work) =>
            {
                document.Checkpoint();
                int m = from + k;
                var entry = document._mcus[m];
                if (entry.Status != McuStatus.Ok)
                    return work;

                // Edge samples = the cached samples without DC + each block's DC term (DC × q0 / 8, exact for the IDCT).
                var ac = document.AcEdgesOf(entry, work.Samples);
                var dc = work.Dc;
                for (int b = 0; b < blocks; b++)
                {
                    int c = document.Layout.Blocks[b].ComponentIndex;
                    dc[b] = entry.Coefficients[b * 64] * document._quant[c][0] / 8f;
                }

                var top = new float[rowLength];
                var bottom = new float[rowLength];
                var columns = new float[columnLength];
                for (int c = 0; c < components.Count; c++)
                {
                    int width = layout.Width[c], height = layout.Height[c];
                    var blockAt = layout.BlockAt[c];
                    int r = RowOffsets[c];
                    for (int x = 0; x < width; x++)
                    {
                        top[r + x] = ac[layout.TopOffset + r + x] + dc[blockAt[0, x / 8]];
                        bottom[r + x] = ac[layout.BottomOffset + r + x] + dc[blockAt[height / 8 - 1, x / 8]];
                    }

                    // The last MCU of a row may be partly outside the picture: use its last visible columns, not padding.
                    bool partial = document.VisibleColumns(m, c) < width && layout.LastVisible[c] < width;
                    int right1 = partial ? layout.LastVisible[c] - 1 : width - 1, right2 = right1 - 1;
                    int source = layout.ColumnsOffset + layout.ColumnOffsets[c];
                    int rightSource = source + (partial ? 4 : 2) * height;
                    int o = columnOffsets[c];
                    for (int y = 0; y < height; y++)
                    {
                        int by = y / 8;
                        columns[o + y] = ac[source + y] + dc[blockAt[by, 0]];
                        columns[o + height + y] = ac[source + height + y] + dc[blockAt[by, 0]];
                        columns[o + 2 * height + y] = ac[rightSource + y] + dc[blockAt[by, right1 / 8]];
                        columns[o + 3 * height + y] = ac[rightSource + height + y] + dc[blockAt[by, right2 / 8]];
                    }
                }

                _top[k] = top;
                _bottom[k] = bottom;
                _columns[k] = columns;
                return work;
            }, _ => { });

            // Seam between stream neighbours k-1 (left) and k: both edges extrapolated to the seam, mean absolute
            // mismatch; brightness counts fully, colour at a quarter.
            Parallel.For(0, length, k =>
            {
                document.Checkpoint();
                _seam[k] = float.NaN;
                if (k == 0 || _columns[k - 1] is not { } left || _columns[k] is not { } right)
                    return;
                double total = 0, weights = 0;
                for (int c = 0; c < components.Count; c++)
                {
                    int height = components[c].V * 8;
                    int o = columnOffsets[c];
                    double sum = 0;
                    for (int y = 0; y < height; y++)
                    {
                        float a = left[o + 2 * height + y], a2 = left[o + 3 * height + y];
                        float b = right[o + y], b2 = right[o + height + y];
                        sum += Math.Abs(a + 0.5f * (a - a2) - (b - 0.5f * (b2 - b)));
                    }

                    double weight = c == 0 ? 1 : 0.25;
                    total += weight * sum / height;
                    weights += weight;
                }

                _seam[k] = (float)(total / weights);
            });
        }

        /// <summary>How badly MCU <paramref name="mcu"/> joins the one before it in the stream; null if not comparable.</summary>
        public float? Seam(int mcu)
        {
            int k = mcu - _from;
            return (uint)k < (uint)_seam.Length && !float.IsNaN(_seam[k]) ? _seam[k] : null;
        }

        /// <summary>Where each component's samples start in a row array.</summary>
        public int[] RowOffsets { get; }

        public float[]? Top(int mcu) => (uint)(mcu - _from) < (uint)_top.Length ? _top[mcu - _from] : null;

        public float[]? Bottom(int mcu) => (uint)(mcu - _from) < (uint)_bottom.Length ? _bottom[mcu - _from] : null;
    }

    /// <summary>
    /// Buckets one row's worth of unshifted boundary differences from a place and compares the biggest bucket with the
    /// bucket around 0. Not thread-safe (reuses its histograms); use one per thread.
    /// </summary>
    private sealed class ColourWindow(JpegDocument document, BoundaryRows rows)
    {
        private readonly int[][] _histograms = document.Frame.Components.Select(_ => new int[HistogramSize]).ToArray();

        /// <summary>Improvement (combined like the fit) and the start of the biggest bucket per component; Offsets null if not comparable.</summary>
        public (double Improvement, int[]? Offsets) Improvement(int index)
        {
            var frame = document.Frame;
            int perRow = frame.McusPerRow;
            int count = Math.Min(document._mcus.Count, frame.McuCount);
            int components = _histograms.Length;
            int end = Math.Min(index + perRow, count);
            var counts = new int[components];
            int expected = 0;
            foreach (var histogram in _histograms)
                Array.Clear(histogram);

            for (int p = index; p < end; p++)
            {
                expected += document.VisibleColumns(p, 0);
                var above = rows.Bottom(p - perRow);
                var current = rows.Top(p);
                if (above == null || current == null)
                    continue;
                for (int c = 0; c < components; c++)
                {
                    int columns = document.VisibleColumns(p, c);
                    int offset = rows.RowOffsets[c];
                    for (int x = 0; x < columns; x++)
                        _histograms[c][Bin(above[offset + x] - current[offset + x])]++;
                    counts[c] += columns;
                }
            }

            if (counts[0] < Math.Max(16, expected / 4))
                return (0, null);

            var offsets = new int[components];
            var best = new double[Math.Max(3, components)];
            var zero = new double[Math.Max(3, components)];
            for (int c = 0; c < components; c++)
            {
                int width = document.BucketWidth(c);
                offsets[c] = BiggestBucketStart(_histograms[c], width);
                best[c] = Share(_histograms[c], offsets[c], width, counts[c]);
                zero[c] = Share(_histograms[c], -(width / 2), width, counts[c]);
            }

            double Combine(double[] f) => components >= 3 ? (f[0] + 0.5 * (f[1] + f[2]) / 2) / 1.5 : f[0];
            return (Combine(best) - Combine(zero), offsets);
        }

        private static double Share(int[] histogram, int start, int width, int total)
        {
            if (total == 0)
                return 0;
            int sum = 0;
            for (int v = start; v <= start + width; v++)
            {
                int bin = v + HistogramOffset;
                if (bin >= 0 && bin < HistogramSize)
                    sum += histogram[bin];
            }

            return (double)sum / total;
        }
    }

    /// <summary>Scores shifts at a place. Not thread-safe (reuses its histograms); use one per thread.</summary>
    private sealed class AlignmentScorer
    {
        private readonly JpegDocument _document;
        private readonly BoundaryRows _rows;
        private readonly int[][] _histograms;
        private readonly int[] _counts;
        private readonly int[] _minBin;
        private readonly int[] _maxBin;
        private readonly int[] _width;

        private readonly int _excess;

        public AlignmentScorer(JpegDocument document, BoundaryRows rows)
        {
            _document = document;
            _rows = rows;
            _excess = document.DataMcuCount() - document.Frame.McuCount;
            int components = document.Frame.Components.Count;
            _histograms = new int[components][];
            for (int c = 0; c < components; c++)
                _histograms[c] = new int[HistogramSize];
            _counts = new int[components];
            _minBin = new int[components];
            _maxBin = new int[components];
            _width = new int[components];
            for (int c = 0; c < components; c++)
                _width[c] = document.BucketWidth(c);
        }

        public AlignmentEstimate? Estimate(int index, bool keepCandidates)
        {
            var frame = _document.Frame;
            int perRow = frame.McusPerRow;
            int count = Math.Min(_document._mcus.Count, frame.McuCount);
            if (index < perRow || index >= count)
                return null;

            int end = Math.Min(index + perRow, count);
            int minimum = Math.Max(16, (end - index) * frame.Components[0].H * 8 / 4);

            int maxShift = perRow / 2;
            var fits = Fits(index, end, maxShift, minimum);
            var candidates = keepCandidates ? new List<AlignmentCandidate>() : null;
            var bonus = SeamBonus(index, maxShift);
            int bestShift = 0;
            double bestScore = double.MinValue, bestFit = -1, unshiftedFit = 0, bestPenalty = 0;
            bool any = false;

            for (int shift = -maxShift; shift <= maxShift; shift++)
            {
                var fit = fits[shift + maxShift];
                if (fit == null)
                    continue;

                any = true;
                candidates?.Add(new AlignmentCandidate(shift, fit.Value));
                double penalty = Penalty(shift);
                double score = fit.Value + (bonus?[shift + maxShift] ?? 0) - penalty;
                if (shift == 0)
                    unshiftedFit = fit.Value;

                if (score > bestScore || (score == bestScore && Math.Abs(shift) < Math.Abs(bestShift)))
                {
                    bestScore = score;
                    bestFit = fit.Value;
                    bestShift = shift;
                    bestPenalty = penalty;
                }
            }

            return any
                ? new AlignmentEstimate(bestShift, bestFit, unshiftedFit,
                    (IReadOnlyList<AlignmentCandidate>?)candidates ?? Array.Empty<AlignmentCandidate>(), bestPenalty)
                : null;
        }

        /// <summary>Count penalty of a shift (see <see cref="CountPenaltyPerHalfRow"/>).</summary>
        public double Penalty(int shift)
        {
            int moved = Math.Abs(_excess + shift) - Math.Abs(_excess);
            return moved <= 0 ? 0 : CountPenaltyPerHalfRow * moved / (_document.Frame.McusPerRow / 2.0);
        }

        /// <summary>The plain fit of one shift at a place; null if too little can be compared.</summary>
        public double? FitAt(int index, int shift)
        {
            var frame = _document.Frame;
            int perRow = frame.McusPerRow;
            int count = Math.Min(_document._mcus.Count, frame.McuCount);
            if (index < perRow || index >= count)
                return null;

            int end = Math.Min(index + perRow, count);
            return Fit(index, end, shift, Math.Max(16, (end - index) * frame.Components[0].H * 8 / 4));
        }

        /// <summary>
        /// The fit of every shift at a place, from the cache if the same MCUs are still around it (in the same order,
        /// with the same DC differences between them: a DC change applied to all of them does not change the fit);
        /// otherwise computed and cached. Fix all repeats the search after every fix, and a fix changes only the
        /// places around it: everything before it is untouched and everything after it has only moved.
        /// </summary>
        private double?[] Fits(int index, int end, int maxShift, int minimum)
        {
            var mcus = _document._mcus;
            int perRow = _document.Frame.McusPerRow;
            int lo = index - perRow, hi = index + perRow + maxShift;   // the MCUs the fits look at
            bool cacheable = end == index + perRow && lo >= 0 && hi <= mcus.Count;
            var cache = _document._fitCache;
            if (cacheable && cache.TryGetValue(mcus[index], out var cached) && cached.Matches(_document, lo))
                return cached.Fits;

            var fits = new double?[2 * maxShift + 1];
            for (int shift = -maxShift; shift <= maxShift; shift++)
            {
                _document.Checkpoint();
                fits[shift + maxShift] = Fit(index, end, shift, minimum);
            }

            if (cacheable)
            {
                if (cache.Count > MaxCachedPlaces)
                    cache.Clear();
                cache[mcus[index]] = CachedFits.Capture(_document, lo, hi, fits);
            }

            return fits;
        }

        /// <summary>
        /// Line-start bonus per shift (index shift + maxShift): the seams of the next <see cref="SeamRows"/> rows after
        /// <paramref name="index"/> are turned into robust z-scores (distance from the median in median absolute
        /// deviations), and a shift gets <see cref="SeamWeight"/> × tanh(z / 10) for the average z of the MCUs it would
        /// put at column 0. Null if too few seams can be compared.
        /// </summary>
        private double[]? SeamBonus(int index, int maxShift)
        {
            int perRow = _document.Frame.McusPerRow;
            int to = Math.Min(index + SeamRows * perRow, _document._mcus.Count);
            var positions = new List<int>();
            var values = new List<double>();
            for (int p = index + 1; p < to; p++)
            {
                if (_rows.Seam(p) is { } h)
                {
                    positions.Add(p);
                    values.Add(h);
                }
            }

            if (values.Count == 0 || values.Count < perRow / 2)
                return null;

            double median = Median(values.ToArray());
            var deviations = values.Select(v => Math.Abs(v - median)).ToArray();
            double spread = Median(deviations) + 1e-3;

            var sum = new double[perRow];
            var count = new int[perRow];
            for (int k = 0; k < positions.Count; k++)
            {
                int column = positions[k] % perRow;
                sum[column] += (values[k] - median) / spread;
                count[column]++;
            }

            var bonus = new double[2 * maxShift + 1];
            for (int shift = -maxShift; shift <= maxShift; shift++)
            {
                int column = ((-shift) % perRow + perRow) % perRow; // the MCUs this shift puts at column 0
                if (count[column] > 0)
                    bonus[shift + maxShift] = SeamWeight * Math.Tanh(sum[column] / count[column] / 10);
            }

            return bonus;
        }

        private static double Median(double[] values)
        {
            Array.Sort(values);
            int n = values.Length;
            return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
        }

        private double? Fit(int index, int end, int shift, int minimum)
        {
            var frame = _document.Frame;
            int perRow = frame.McusPerRow;
            int components = _histograms.Length;
            var offsets = _rows.RowOffsets;
            for (int c = 0; c < components; c++)
            {
                _counts[c] = 0;
                _minBin[c] = HistogramSize;
                _maxBin[c] = -1;
            }

            for (int p = index; p < end; p++)
            {
                if (shift > 0 && p < index + shift)
                    continue; // a blank MCU would be inserted here

                int source = p - shift;
                if (source >= _document._mcus.Count)
                    continue;
                var above = _rows.Bottom(p - perRow);
                var current = _rows.Top(source);
                if (above == null || current == null)
                    continue;

                for (int c = 0; c < components; c++)
                {
                    var histogram = _histograms[c];
                    // Full MCU width, also for the last MCU of a row: this makes the fit independent of
                    // the column, so it can be cached when MCUs only move.
                    int columns = _document.Frame.Components[c].H * 8;
                    int offset = offsets[c];
                    for (int x = 0; x < columns; x++)
                    {
                        int bin = Bin(above[offset + x] - current[offset + x]);
                        histogram[bin]++;
                        if (bin < _minBin[c]) _minBin[c] = bin;
                        if (bin > _maxBin[c]) _maxBin[c] = bin;
                    }

                    _counts[c] += columns;
                }
            }

            double? result = null;
            if (_counts[0] >= minimum)
            {
                double fit = Bucket(0);
                if (components >= 3)
                    fit = (fit + 0.5 * (Bucket(1) + Bucket(2)) / 2) / 1.5;
                result = fit;
            }

            for (int c = 0; c < components; c++)
            {
                if (_maxBin[c] >= 0)
                    Array.Clear(_histograms[c], _minBin[c], _maxBin[c] - _minBin[c] + 1);
            }

            return result;
        }

        /// <summary>Share of values in the fullest window of the bucket width.</summary>
        private double Bucket(int component)
        {
            if (_counts[component] == 0)
                return 0;

            var histogram = _histograms[component];
            int width = _width[component];
            int from = _minBin[component], to = _maxBin[component];
            int sum = 0, best = 0;
            for (int i = from; i <= to; i++)
            {
                sum += histogram[i];
                if (i - width - 1 >= from)
                    sum -= histogram[i - width - 1];
                if (sum > best)
                    best = sum;
            }

            return (double)best / _counts[component];
        }
    }
}
