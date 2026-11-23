namespace JpegRepair.Core;

/// <summary>How the user has marked an MCU, overriding bad-MCU detection (see <see cref="JpegDocument.SetMark"/>).</summary>
public enum McuMark : byte
{
    /// <summary>Not marked: detection decides.</summary>
    Auto,

    /// <summary>Bad, whatever detection finds.</summary>
    Bad,

    /// <summary>Not bad, although detection flags it.</summary>
    NotBad,
}

/// <summary>
/// Result of <see cref="BadMcuDetector.Detect"/>: which picture MCUs look bad, and which of their edges are rugged.
/// With the user's marks applied (see <see cref="JpegDocument.SetMark"/>), <see cref="IsBad"/> is the final answer and
/// <see cref="IsDetected"/> what detection found on its own.
/// </summary>
public sealed class BadMcuMap
{
    public const byte Top = 1, Right = 2, Bottom = 4, Left = 8;

    private readonly byte[] _rugged;   // per MCU: bit set of rugged edges (Top/Right/Bottom/Left)
    private readonly bool[] _detected;
    private readonly bool[] _bad;
    private readonly McuMark[]? _marks;

    internal BadMcuMap(byte[] rugged, bool[] bad, int count)
    {
        _rugged = rugged;
        _detected = bad;
        _bad = bad;
        Count = count;
    }

    private BadMcuMap(BadMcuMap detected, bool[] bad, int count, McuMark[] marks, int markedBad, int markedNotBad)
    {
        _rugged = detected._rugged;
        _detected = detected._detected;
        _bad = bad;
        _marks = marks;
        Count = count;
        MarkedBadCount = markedBad;
        MarkedNotBadCount = markedNotBad;
    }

    public static BadMcuMap Empty(int mcuCount) => new(new byte[mcuCount], new bool[mcuCount], 0);

    /// <summary>Number of MCUs that are bad (detected and not marked as not bad, or marked as bad).</summary>
    public int Count { get; }

    /// <summary>Number of MCUs marked as bad.</summary>
    public int MarkedBadCount { get; }

    /// <summary>Number of MCUs marked as not bad that detection flags (marks that change the result).</summary>
    public int MarkedNotBadCount { get; }

    /// <summary>Number of marks in effect (<see cref="MarkedBadCount"/> + <see cref="MarkedNotBadCount"/>).</summary>
    public int MarkCount => MarkedBadCount + MarkedNotBadCount;

    /// <summary>True if the MCU counts as bad, taking the user's marks into account.</summary>
    public bool IsBad(int index) => (uint)index < (uint)_bad.Length && _bad[index];

    /// <summary>True if detection flagged the MCU, whatever the user's mark.</summary>
    public bool IsDetected(int index) => (uint)index < (uint)_detected.Length && _detected[index];

    /// <summary>
    /// The user's mark on the MCU, if it is in effect: <see cref="McuMark.NotBad"/> only while detection flags the MCU
    /// (<see cref="McuMark.Auto"/> otherwise).
    /// </summary>
    public McuMark Mark(int index) => _marks != null && (uint)index < (uint)_marks.Length ? _marks[index] : McuMark.Auto;

    /// <summary>Rugged edges of an MCU as a combination of <see cref="Top"/>, <see cref="Right"/>, <see cref="Bottom"/>, <see cref="Left"/>.</summary>
    public byte RuggedEdges(int index) => (uint)index < (uint)_rugged.Length ? _rugged[index] : (byte)0;

    /// <summary>
    /// This (detected) map with the user's marks applied: <see cref="McuMark.Bad"/> makes an MCU bad,
    /// <see cref="McuMark.NotBad"/> makes a flagged MCU good.
    /// </summary>
    internal BadMcuMap WithMarks(Func<int, McuMark> mark)
    {
        McuMark[]? marks = null;
        bool[] bad = _detected;
        int count = Count, markedBad = 0, markedNotBad = 0;
        for (int i = 0; i < _detected.Length; i++)
        {
            var m = mark(i);
            if (m == McuMark.Auto || (m == McuMark.NotBad && !_detected[i]))
                continue;
            if (marks == null)
            {
                marks = new McuMark[_detected.Length];
                bad = (bool[])_detected.Clone();
            }

            marks[i] = m;
            if (m == McuMark.Bad)
            {
                markedBad++;
                if (!bad[i])
                    count++;
                bad[i] = true;
            }
            else
            {
                markedNotBad++;
                count--;
                bad[i] = false;
            }
        }

        return marks == null ? this : new BadMcuMap(this, bad, count, marks, markedBad, markedNotBad);
    }

    public IEnumerable<int> BadIndices()
    {
        for (int i = 0; i < _bad.Length; i++)
        {
            if (_bad[i])
                yield return i;
        }
    }
}

/// <summary>
/// Finds MCUs that don't fit the picture around them: garbage decoded from damaged data, or MCUs that were
/// inserted (data from elsewhere). Rule: look at the edges to the 4 neighbouring MCUs; if 2 or more are
/// rugged, and at least one of them is the edge above or below, the MCU is bad. Colour drift and misalignment make
/// only one edge per MCU rugged (the row boundary above the shifted part), so they are not flagged, except the single
/// MCU where such a shift starts. Requiring an edge above or below drops most false alarms: in intact pictures they
/// are mostly thin vertical details along an MCU border (rugged left and right only), while garbage nearly always
/// breaks against the row above or below too.
///
/// An edge is rugged when the step across it (mean colour difference between the two pixel lines that meet there)
/// is both
///   - more than 3 × the median step over all MCU edges of the picture (so detailed pictures need bigger steps), and
///   - more than <c>factor</c> × (step just inside the smoother of the two MCUs + 2), so a sharp but real edge that
///     continues into the MCU doesn't count.
/// Difference = |ΔY| + ½ (|ΔCb| + |ΔCr|). Edges to Missing or Blank MCUs are ignored (they are marked anyway).
///
/// The colour estimate (<see cref="JpegDocument.EstimateDcShift(int)"/>) leaves flagged MCUs out. The alignment search
/// does not: judged where they are now, intact MCUs next to a misalignment get two rugged edges too; judged per
/// candidate shift, the MCUs that do not fit a wrong shift would drop out of its score, so wrong shifts would score as
/// well as the right one.
/// </summary>
public static class BadMcuDetector
{
    private const float PictureFactor = 3f;
    private const float InsideFloor = 2f;
    private const float MinBase = 1f;

    /// <summary>An edge is rugged if its step is more than this × (the step just inside + 2). See the class summary.</summary>
    public const float EdgeFactor = 4f;

    /// <summary>
    /// Detects bad MCUs in a rendered picture.
    /// </summary>
    /// <param name="bgra">The picture, <paramref name="frame"/>.Width × Height BGRA, stride Width × 4.</param>
    /// <param name="status">Status of the MCU shown at each picture position (0 .. McuCount-1).</param>
    public static BadMcuMap Detect(byte[] bgra, FrameHeader frame, Func<int, McuStatus> status)
    {
        int count = frame.McuCount;
        if (count == 0)
            return BadMcuMap.Empty(count);

        int perRow = frame.McusPerRow, rows = frame.McuRows;
        int mw = frame.McuWidth, mh = frame.McuHeight;
        int width = frame.Width, height = frame.Height;
        if (bgra.Length < width * height * 4)
            throw new ArgumentException("Picture is smaller than the frame.", nameof(bgra));

        var real = new bool[count];
        for (int i = 0; i < count; i++)
            real[i] = status(i) is McuStatus.Ok or McuStatus.Corrupt or McuStatus.Filled;

        // Only the two pixel lines on each side of every MCU edge are read, so this is cheap even for big pictures.
        var pixels = bgra;

        // Horizontal edges: between MCU (r, c) and (r + 1, c). Vertical edges: between (r, c) and (r, c + 1).
        // NaN = not compared.
        var hStep = new float[Math.Max(0, rows - 1) * perRow];
        var hInside = new float[hStep.Length];
        var vStep = new float[rows * Math.Max(0, perRow - 1)];
        var vInside = new float[vStep.Length];
        hStep.AsSpan().Fill(float.NaN);
        vStep.AsSpan().Fill(float.NaN);

        Parallel.For(0, rows, r =>
        {
            int y0 = r * mh;
            int rowHeight = Math.Min(mh, height - y0);

            // Edge below this MCU row.
            int yb = y0 + mh;   // first line of the next row
            if (r + 1 < rows && yb + 1 < height && rowHeight >= 2)
            {
                for (int c = 0; c < perRow; c++)
                {
                    int top = r * perRow + c, bottom = top + perRow;
                    if (!real[top] || !real[bottom])
                        continue;
                    int x0 = c * mw, x1 = Math.Min(x0 + mw, width);
                    float step = 0, gA = 0, gB = 0;
                    for (int x = x0; x < x1; x++)
                    {
                        step += Difference(pixels, width, x, yb - 1, x, yb);
                        gA += Difference(pixels, width, x, yb - 2, x, yb - 1);
                        gB += Difference(pixels, width, x, yb, x, yb + 1);
                    }

                    int n = x1 - x0;
                    hStep[r * perRow + c] = step / n;
                    hInside[r * perRow + c] = Math.Min(gA, gB) / n;
                }
            }

            // Edges to the right of each MCU in this row.
            if (rowHeight >= 1 && perRow > 1)
            {
                for (int c = 0; c + 1 < perRow; c++)
                {
                    int left = r * perRow + c, right = left + 1;
                    int xb = (c + 1) * mw;   // first column of the right MCU
                    if (!real[left] || !real[right] || xb + 1 >= width)
                        continue;
                    float step = 0, gA = 0, gB = 0;
                    for (int y = y0; y < y0 + rowHeight; y++)
                    {
                        step += Difference(pixels, width, xb - 1, y, xb, y);
                        gA += Difference(pixels, width, xb - 2, y, xb - 1, y);
                        gB += Difference(pixels, width, xb, y, xb + 1, y);
                    }

                    int idx = r * (perRow - 1) + c;
                    vStep[idx] = step / rowHeight;
                    vInside[idx] = Math.Min(gA, gB) / rowHeight;
                }
            }
        });

        // Typical step of this picture.
        var all = new List<float>(hStep.Length + vStep.Length);
        foreach (float s in hStep)
            if (!float.IsNaN(s)) all.Add(s);
        foreach (float s in vStep)
            if (!float.IsNaN(s)) all.Add(s);
        if (all.Count == 0)
            return BadMcuMap.Empty(count);
        all.Sort();
        float baseStep = Math.Max(all[all.Count / 2], MinBase);

        const float factor = EdgeFactor;
        bool Rugged(float step, float inside) =>
            !float.IsNaN(step) && step > PictureFactor * baseStep && step > factor * (inside + InsideFloor);

        var rugged = new byte[count];
        for (int r = 0; r + 1 < rows; r++)
        {
            for (int c = 0; c < perRow; c++)
            {
                int k = r * perRow + c;
                if (Rugged(hStep[k], hInside[k]))
                {
                    rugged[k] |= BadMcuMap.Bottom;
                    rugged[k + perRow] |= BadMcuMap.Top;
                }
            }
        }

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c + 1 < perRow; c++)
            {
                int k = r * (perRow - 1) + c;
                if (Rugged(vStep[k], vInside[k]))
                {
                    rugged[r * perRow + c] |= BadMcuMap.Right;
                    rugged[r * perRow + c + 1] |= BadMcuMap.Left;
                }
            }
        }

        var bad = new bool[count];
        int badCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (real[i] && System.Numerics.BitOperations.PopCount(rugged[i]) >= 2
                && (rugged[i] & (BadMcuMap.Top | BadMcuMap.Bottom)) != 0)
            {
                bad[i] = true;
                badCount++;
            }
        }

        return new BadMcuMap(rugged, bad, badCount);
    }

    /// <summary>|ΔY| + ½ (|ΔCb| + |ΔCr|) between two BGRA pixels (BT.601 full range, as JPEG uses).</summary>
    private static float Difference(byte[] p, int width, int xa, int ya, int xb, int yb)
    {
        int a = (ya * width + xa) * 4, b = (yb * width + xb) * 4;
        float dB = p[a] - p[b], dG = p[a + 1] - p[b + 1], dR = p[a + 2] - p[b + 2];
        float dy = 0.299f * dR + 0.587f * dG + 0.114f * dB;
        float dcb = -0.168736f * dR - 0.331264f * dG + 0.5f * dB;
        float dcr = 0.5f * dR - 0.418688f * dG - 0.081312f * dB;
        return MathF.Abs(dy) + 0.5f * (MathF.Abs(dcb) + MathF.Abs(dcr));
    }
}
