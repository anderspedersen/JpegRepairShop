using System.Runtime.Intrinsics;

namespace JpegRepair.Core;

/// <summary>
/// One step planned by <see cref="JpegDocument.PlanDpFix"/>, applied by <see cref="JpegDocument.ApplyDpStep"/>:
/// an edit (insert <see cref="Shift"/> blanks before <see cref="Index"/> if Shift &gt; 0, remove −Shift MCUs at
/// <see cref="Index"/> if Shift &lt; 0, then fix the colours there), or, with Shift 0, a colour start: a colour shift
/// from <see cref="Index"/> on, estimated from the MCUs <see cref="Index"/>..<see cref="ColourEnd"/> − 1. Indices count
/// with all earlier steps of the plan applied, so the steps are applied in order.
/// </summary>
public readonly record struct DpStep(int Index, int Shift, int ColourEnd = 0)
{
    public bool IsEdit => Shift != 0;
}

/// <summary>
/// How thoroughly <see cref="JpegDocument.PlanDpFix"/> searches: <see cref="Fast"/> keeps one path per state and never
/// goes back; <see cref="Slow"/> and <see cref="VerySlow"/> keep 5 and 15 paths per state and go back where rows end up
/// shifted against the picture's edges (see the remarks on <see cref="JpegDocument.PlanDpFix"/>).
/// </summary>
public enum FixAllSpeed
{
    Fast,
    Slow,
    VerySlow,
}

/// <summary>The result of <see cref="JpegDocument.PlanDpFix"/>: the steps in the order to apply them.</summary>
/// <param name="Thumbnail">The embedded preview the search used (<see cref="JpegDocument.FindThumbnail"/>), or null.</param>
public sealed record DpPlan(IReadOnlyList<DpStep> Steps, double Cost, ThumbnailReference? Thumbnail = null)
{
    public IReadOnlyList<DpStep> Edits => Steps.Where(s => s.IsEdit).ToList();

    public IReadOnlyList<DpStep> ColourStarts => Steps.Where(s => !s.IsEdit).ToList();

    public int Inserted => Steps.Where(s => s.Shift > 0).Sum(s => s.Shift);

    public int Removed => Steps.Where(s => s.Shift < 0).Sum(s => -s.Shift);
}

/// <summary>
/// "Fix all": which MCUs of the stream to remove and where to insert blanks, chosen for the whole picture at once
/// by dynamic programming.
///
/// State (x, y): the first x picture positions are filled, using the first y MCUs of the stream (y − x is kept between
/// −1 and +3 rows, but at least −256 and +1024 MCUs; <see cref="DpOffsetRange"/>). Each step places the next stream MCU, inserts a blank or removes the next stream MCU. Placing MCU y at
/// position x costs:
/// <list type="bullet">
/// <item>the seam to the MCU placed at x − W on the best path to the state (W = MCUs per row): per component, the
/// difference between the top sample row and the bottom sample row above, minus its median (the best colour offset),
/// mean absolute value, Y + ½(Cb + Cr);</item>
/// <item>a colour term: the size of the best offset, or of its change from the previous MCU's best offset if that is
/// smaller, so a run of MCUs with the same wrong colour pays once;</item>
/// <item>the seam to the MCU placed at x − 1 in the same way (left/right sample columns, offset removed), except at
/// the start of a row: a shift left in the picture breaks every row there;</item>
/// <item>at most <see cref="DpCap"/>, so garbage and strong picture edges cost a bounded amount;</item>
/// <item>at the start of a row, where the MCU before is at the end of the row above: the picture's right and left
/// edges usually look different, while two MCUs that were neighbours in the stream look alike when the rows are
/// shifted. Their difference here is the seam plus half the size of the colour offset; if it is below
/// <see cref="DpRowStartThreshold"/>, the shortfall times <see cref="DpRowStartWeight"/> is added (on top of the
/// cap).</item>
/// </list>
/// Edits come in runs: the first MCU of a run of removals or inserts costs <see cref="DpOpen"/>, each further removed
/// MCU <see cref="DpRemoveEach"/> and each further blank <see cref="DpCap"/> (as much as the worst MCU; the MCU below a
/// blank also costs <see cref="DpCap"/>), so swapping MCUs for blanks never pays. A run of removals is never directly followed by
/// a run of inserts or the other way round. All costs are in units of the median seam of the stream as it is (each MCU
/// against the one a row earlier). Without a preview the first row is taken as it is; with one, the first row can be
/// edited too, judged by the left seams and the preview alone (it has nothing above it). Removing what is left of the
/// stream after the last position is free. Neither those MCUs nor blanks after the last placed MCU are part of the plan.
///
/// Colour starts (only when asked for): along the final path, every placed MCU has its best colour offsets against the MCU above. Where they
/// change by more than <see cref="DpColourJump"/> levels from the previous MCU's and the next
/// <see cref="DpColourFollow"/> MCUs change by less than <see cref="DpColourSmall"/> each, a colour shift may start.
/// Its fix is the colour estimate against the row above over the MCUs up to the next such place (at most one row's worth), applied if
/// it comes to at least 4 levels (<see cref="FixColourShiftOver"/>). Places where an edit already fixes the colours
/// are left out.
///
/// Each state keeps up to 1, 5 or 15 paths (<see cref="FixAllSpeed"/>): the cheapest ones with different numbers of
/// removed MCUs (and
/// so, at the same y − x, different numbers of blanks). Costs don't depend on that number; it only keeps paths with
/// different edits from being merged into one.
///
/// Paths that reach the same state with the same number of removed MCUs are merged, keeping the cheaper one so far.
/// That can lock in a pattern: once a row has edits that cancel out within it, repeating them in every row below costs
/// a few edits per row, while dropping them costs one row of mismatch at once and only pays off in the rows after it,
/// so the path that drops them is always the one thrown away. After the search,
/// <see cref="DpSearch.DropRepeatedPatterns"/> therefore tries dropping each run of such rows and keeps the change if
/// the exact cost of the whole layout goes down.
///
/// Merging can also lose the path that ends a damaged stretch with the rows lined up against the picture's edges,
/// for the same reason. During a Slow or Very slow search, after each row the best path's last
/// <see cref="DpBacktrackRows"/> rows are
/// therefore checked (<see cref="DpSearch.FindMisalignment"/>): if at every row start the MCU and the one before it
/// look alike, and the rows have a big jump between neighbouring MCUs (at least <see cref="DpBacktrackJump"/> times the
/// median difference between stream neighbours) at the same column, that jump is taken to be the picture's edge. The
/// search then goes back to the start of those rows, lowers the cost of every state whose next MCU is the one after
/// the jump (or a whole number of rows from it) by <see cref="DpBacktrackBonus"/> and goes on from there. It does not
/// check again until it is <see cref="DpBacktrackRows"/> rows past that point, and backtracks at most
/// <see cref="DpBacktrackMax"/> times.
///
/// With an embedded preview that registers against the top of the picture (<see cref="FindThumbnail"/>), the stream is
/// cut into runs (between corrupt MCUs, at most a row long), and for each run and each y − x the preview says how well
/// the whole run fits there: the mean, over the run's MCUs, of the difference between the preview's mean levels at x
/// and the MCU's (averaged with its two stream neighbours), after taking away the colour offset that fits the run best
/// at that y − x, Y + ½(Cb + Cr), at most <see cref="DpThumbCap"/> levels per MCU (an MCU the preview doesn't cover counts
/// as the cap). The preview is compared at the same y − x and at whole MCU rows from it, up to one preview pixel's worth,
/// and the best counts: a small preview, registered on a few rows, can't tell the rows apart that finely. Placing an MCU
/// costs <see cref="DpThumbWeight"/> × how much worse its run fits at that y − x than at the run's best one; a blank
/// costs <see cref="DpThumbBlank"/>. Taking the mean over the whole run matters: compared MCU by MCU, the preview's
/// noise (a few levels per MCU) is larger than the seams' unit on a clean photo, and the search then trades seams for
/// small gains on the preview, deleting and inserting MCUs in rows that are not damaged at all.
///
/// What is above a position depends on the path, so the DP keeps, per state, a pointer into a shared list of edit
/// records (position, y − x after it, insert or not); the MCU at x − W is found by walking back to the record in effect
/// there. Each record also points further back on its path, so long runs of edits are skipped quickly. The list is
/// compacted every row, keeping the records of every path still in the search completely; the final layout is read
/// from the best path's records, so only the current and the next column of states are kept.
/// </summary>
public sealed partial class JpegDocument
{
    private const float DpCap = 8f;
    private const float DpOpen = 15f;
    private const float DpRemoveEach = 0.5f;
    private const float DpColourWeight = 0.5f;
    private const float DpLeftSeamWeight = 0.5f;
    private const float DpRowStartWeight = 1f;
    private const float DpRowStartThreshold = 4f;
    private const float DpRowStartColourShare = 0.5f;
    private const int DpPatternRounds = 20;
    private const int DpBacktrackRows = 3;
    private const float DpBacktrackJump = 8f;     // times the median difference of stream neighbours
    private const float DpBacktrackBonus = 150f;
    private const int DpBacktrackMax = 100;
    private const int DpRowsBack = 1;     // y − x down to −1 row (inserts)
    private const int DpRowsAhead = 3;    // y − x up to +3 rows (removals)
    private const int DpMinBack = 256;    // ... but at least this many MCUs (narrow pictures)
    private const int DpMinAhead = 1024;
    private const float DpMinUnit = 0.25f;
    private const long DpMaxStates = 1_500_000_000;
    private const float DpColourJump = 10f;
    private const float DpColourSmall = 6f;
    private const int DpColourFollow = 5;
    private const float DpThumbWeight = 1f;
    private const float DpThumbCap = 16f;   // levels
    private const float DpThumbBlank = 8f;  // levels
    private const int DpThumbMinRun = 8;

    private const int LayerPlaced = 0, LayerInserted = 1, LayerRemoved = 2;
    private const float NoPath = float.PositiveInfinity;

    /// <summary>
    /// Plans the inserts and removals for the whole picture (see the class remarks). Does not change the document.
    /// <paramref name="progress"/> gets (rows done, rows) once per MCU row. Pauses and stops at <see cref="Gate"/>.
    /// </summary>
    /// <param name="colourStarts">Also plan colour fixes where the path shows a colour shift starting.</param>
    /// <param name="useThumbnail">Use an embedded preview if one registers (see the class remarks).</param>
    /// <param name="speed">How thoroughly to search.</param>
    public DpPlan PlanDpFix(Action<int, int>? progress = null, bool colourStarts = false, bool useThumbnail = true,
        FixAllSpeed speed = FixAllSpeed.Fast)
    {
        int perRow = Frame.McusPerRow;
        int positions = Frame.McuCount;
        int streamLength = DataMcuCount();
        if (positions <= perRow || streamLength <= perRow)
            return new DpPlan([], 0);

        var samples = DpSamples.Build(this, streamLength);
        Checkpoint();
        samples.CacheNeighbourSeams(perRow);
        Checkpoint();
        float unit = samples.MedianSeam(perRow);

        var (offMin, offMax) = DpOffsetRange();

        var reference = useThumbnail ? FindThumbnail() : null;
        Checkpoint();
        var thumbnail = reference == null
            ? null
            : DpThumbnail.Build(this, reference, streamLength, offMin, offMax, samples.Weights);
        Checkpoint();

        int slots = speed switch { FixAllSpeed.Slow => 5, FixAllSpeed.VerySlow => 15, _ => 1 };
        var dp = new DpSearch(samples, thumbnail, slots, speed != FixAllSpeed.Fast, perRow, positions, streamLength, offMin,
            offMax, unit, Checkpoint, progress);
        var placed = dp.Run(out _);
        Checkpoint();
        placed = dp.DropRepeatedPatterns(placed);
        double cost = dp.LayoutCost(placed, perRow, positions);
        var edits = PlanEdits(placed, dp.First, streamLength);
        List<int> starts = colourStarts ? FindColourStarts(samples, placed, perRow) : [];
        return new DpPlan(MergeSteps(edits, starts, perRow, positions), cost, reference);
    }

    /// <summary>
    /// The range of y − x the search covers: <see cref="DpRowsBack"/> rows back to <see cref="DpRowsAhead"/> rows ahead,
    /// but at least <see cref="DpMinBack"/> and <see cref="DpMinAhead"/> MCUs, so a narrow picture can still lose a big
    /// burst of garbage. Very large pictures get a narrower range, in the same proportion, to stay within
    /// <see cref="DpMaxStates"/>.
    /// </summary>
    private (int Min, int Max) DpOffsetRange()
    {
        int perRow = Frame.McusPerRow;
        int positions = Frame.McuCount;
        int offMin = -Math.Max(DpRowsBack * perRow, DpMinBack);
        int offMax = Math.Max(DpRowsAhead * perRow, DpMinAhead);
        if ((long)(positions + 1) * (offMax - offMin + 1) > DpMaxStates)
        {
            int size = (int)(DpMaxStates / (positions + 1));
            offMin = -size * DpRowsBack / (DpRowsBack + DpRowsAhead);
            offMax = offMin + size - 1;
        }

        return (offMin, offMax);
    }

    /// <summary>
    /// Applies one step of a <see cref="DpPlan"/> (one undo step, or nothing for a colour start that needs no fix).
    /// Returns the DC change per component.
    /// </summary>
    public int[] ApplyDpStep(DpStep step) =>
        step.IsEdit ? AlignAndFixColors(step.Index, step.Shift) : FixColourShiftOver(step.Index, step.ColourEnd);

    /// <summary>
    /// The colour estimate against the row above (as <see cref="AutoFixColors"/>) over the MCUs <paramref name="index"/>..<paramref name="end"/> − 1
    /// instead of one row's worth, applied from <paramref name="index"/> on (one undo step) if it comes to at least
    /// 4 levels. Returns the DC change per component (all zero if nothing was changed).
    /// </summary>
    public int[] FixColourShiftOver(int index, int end)
    {
        if (index < 0 || index >= _mcus.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var deltas = EstimateDcShift(index, ColourFixSkips(), Math.Max(1, end - index));
        float levels = 0;
        for (int c = 0; c < deltas.Length; c++)
            levels = Math.Max(levels, Math.Abs(deltas[c]) * _quant[c][0] / 8f);
        if (levels < MinColourShiftLevels)
            return new int[deltas.Length];

        Apply(new DcEdit(index, deltas, "Fix colour shift"));
        return deltas;
    }

    /// <summary>
    /// Places along the final path where a colour shift may start (see the class remarks): the best offsets against the
    /// MCU above change by more than <see cref="DpColourJump"/> levels, then by less than <see cref="DpColourSmall"/>
    /// for each of the next <see cref="DpColourFollow"/> MCUs.
    /// </summary>
    private static List<int> FindColourStarts(DpSamples samples, int[] placed, int perRow)
    {
        int n = placed.Length, components = samples.Weights.Length;
        var offsets = new float[n * components];
        var has = new bool[n];
        Parallel.For(perRow, n, x =>
        {
            int below = placed[x], above = placed[x - perRow];
            if (below < 0 || above < 0)
                return;
            Span<float> o = stackalloc float[components];
            samples.VerticalSeam(below, above, o);
            o.CopyTo(offsets.AsSpan(x * components, components));
            has[x] = true;
        });

        var change = new float[n];
        Span<float> d = stackalloc float[components];
        for (int x = 0; x < n; x++)
        {
            if (x == 0 || !has[x] || !has[x - 1])
            {
                change[x] = float.NaN;
                continue;
            }

            for (int c = 0; c < components; c++)
                d[c] = offsets[x * components + c] - offsets[(x - 1) * components + c];
            change[x] = samples.Size(d);
        }

        var starts = new List<int>();
        for (int x = perRow + 1; x + DpColourFollow < n; x++)
        {
            if (!(change[x] > DpColourJump))
                continue;
            bool steady = true;
            for (int k = 1; k <= DpColourFollow && steady; k++)
                steady = change[x + k] < DpColourSmall;   // false for NaN too
            if (steady)
                starts.Add(x);
        }

        return starts;
    }

    /// <summary>
    /// The edits and the colour starts in the order of their positions. A colour start is estimated over the MCUs up to
    /// the next colour start, at most one row's worth and at least <see cref="DpColourFollow"/> + 1. Colour starts where
    /// an edit fixes the colours (after a removal, or after inserted blanks) are dropped.
    /// </summary>
    private static List<DpStep> MergeSteps(List<DpStep> edits, List<int> starts, int perRow, int positions)
    {
        var editColour = new HashSet<int>(edits.Select(e => e.Index + Math.Max(e.Shift, 0)));
        var steps = new List<DpStep>(edits.Count + starts.Count);
        int e = 0;
        for (int s = 0; s < starts.Count; s++)
        {
            int x = starts[s];
            while (e < edits.Count && edits[e].Index <= x)
                steps.Add(edits[e++]);
            if (editColour.Contains(x))
                continue;
            int end = Math.Min(x + perRow, s + 1 < starts.Count ? starts[s + 1] : positions);
            end = Math.Min(positions, Math.Max(end, x + DpColourFollow + 1));
            steps.Add(new DpStep(x, 0, end));
        }

        while (e < edits.Count)
            steps.Add(edits[e++]);
        return steps;
    }

    /// <summary>
    /// The colour-only search on its own: the biggest colour shift where nothing is misaligned, or null.
    /// The fix is <see cref="AlignAndFixColors"/> with shift 0.
    /// </summary>
    public MisalignmentResult? FindColourShift()
    {
        if (Math.Min(_mcus.Count, Frame.McuCount) <= Frame.McusPerRow)
            return null;
        Checkpoint();
        return FindColourShift(new BoundaryRows(this, 0, _mcus.Count));
    }

    /// <summary>
    /// Turns the stream index placed at every position (−1 for a blank) into edits, in order. Blanks after the last
    /// placed MCU and stream MCUs after it are left out. Positions before <paramref name="first"/> are as decoded.
    /// </summary>
    private static List<DpStep> PlanEdits(int[] placed, int first, int streamLength)
    {
        var edits = new List<DpStep>();
        int lastPlaced = placed.Length - 1;
        while (lastPlaced >= 0 && placed[lastPlaced] < 0)
            lastPlaced--;

        int expected = first;
        for (int x = first; x <= lastPlaced;)
        {
            int s = placed[x];
            if (s < 0)
            {
                int run = 0;
                while (placed[x + run] < 0)
                    run++;
                edits.Add(new DpStep(x, run));
                x += run;
                continue;
            }

            if (s > expected)
                edits.Add(new DpStep(x, expected - s));
            expected = s + 1;
            x++;
        }

        return edits;
    }

    /// <summary>The sample rows and columns along the edges of every stream MCU, per component.</summary>
    private sealed class DpSamples
    {
        private DpSamples(int count, int[] rowLength, int[] columnLength, float[] weights)
        {
            Count = count;
            RowLength = rowLength;
            ColumnLength = columnLength;
            Weights = weights;
            RowStride = rowLength.Sum();
            ColumnStride = columnLength.Sum();
            Top = new float[(long)count * RowStride];
            Bottom = new float[(long)count * RowStride];
            Left = new float[(long)count * ColumnStride];
            Right = new float[(long)count * ColumnStride];
        }

        public int Count { get; }

        public int[] RowLength { get; }

        public int[] ColumnLength { get; }

        public float[] Weights { get; }

        public int RowStride { get; }

        public int ColumnStride { get; }

        public float[] Top { get; }

        public float[] Bottom { get; }

        public float[] Left { get; }

        public float[] Right { get; }

        public static DpSamples Build(JpegDocument document, int count)
        {
            var components = document.Frame.Components;
            int n = components.Count;
            var rowLength = new int[n];
            var columnLength = new int[n];
            var weights = new float[n];
            for (int c = 0; c < n; c++)
            {
                rowLength[c] = components[c].H * 8;
                columnLength[c] = components[c].V * 8;
                weights[c] = c == 0 ? 1f : 0.5f;
            }

            var result = new DpSamples(count, rowLength, columnLength, weights);
            Parallel.For(0, count, () => new float[document._samplesPerMcu], (i, _, buffer) =>
            {
                document.EntrySamples(document._mcus[i], buffer);
                int row = i * result.RowStride, column = i * result.ColumnStride;
                for (int c = 0; c < n; c++)
                {
                    int plane = document._planeOffset[c], width = rowLength[c], height = columnLength[c];
                    int last = plane + (height - 1) * width;
                    for (int x = 0; x < width; x++)
                    {
                        result.Top[row + x] = buffer[plane + x];
                        result.Bottom[row + x] = buffer[last + x];
                    }

                    for (int y = 0; y < height; y++)
                    {
                        result.Left[column + y] = buffer[plane + y * width];
                        result.Right[column + y] = buffer[plane + y * width + width - 1];
                    }

                    row += width;
                    column += height;
                }

                return buffer;
            }, _ => { });
            return result;
        }

        /// <summary>
        /// Computes, once per stream MCU, its seams to the MCU a row earlier in the stream (as the MCU above) and to the
        /// MCU before it (as its left neighbour). Paths without recent edits need exactly these, so most seams the
        /// search asks for become lookups.
        /// </summary>
        public void CacheNeighbourSeams(int perRow)
        {
            int n = Weights.Length;
            var below = new float[Count];
            var belowOffsets = new float[Count * n];
            var beside = new float[Count];
            var besideOffsets = new float[Count * n];
            Parallel.For(1, Count, y =>
            {
                Span<float> offsets = stackalloc float[n];
                if (y >= perRow)
                {
                    below[y] = Seam(Top, (long)y * RowStride, Bottom, (long)(y - perRow) * RowStride, RowLength, offsets);
                    offsets.CopyTo(belowOffsets.AsSpan(y * n, n));
                }

                beside[y] = Seam(Left, (long)y * ColumnStride, Right, (long)(y - 1) * ColumnStride, ColumnLength, offsets);
                offsets.CopyTo(besideOffsets.AsSpan(y * n, n));
            });
            _cachePerRow = perRow;
            _below = below;
            _belowOffsets = belowOffsets;
            _beside = beside;
            _besideOffsets = besideOffsets;
        }

        private int _cachePerRow;
        private float[]? _below, _belowOffsets, _beside, _besideOffsets;

        /// <summary>Seam between the top of <paramref name="below"/> and the bottom of <paramref name="above"/>; the best offsets go to <paramref name="offsets"/>.</summary>
        public float VerticalSeam(int below, int above, Span<float> offsets)
        {
            if (_below != null && above == below - _cachePerRow && above >= 0)
            {
                int n = Weights.Length;
                _belowOffsets.AsSpan(below * n, n).CopyTo(offsets);
                return _below[below];
            }

            return Seam(Top, (long)below * RowStride, Bottom, (long)above * RowStride, RowLength, offsets);
        }

        /// <summary>Seam between the left of <paramref name="right"/> and the right of <paramref name="left"/>.</summary>
        public float HorizontalSeam(int right, int left, Span<float> offsets)
        {
            if (_beside != null && left == right - 1 && left >= 0)
            {
                int n = Weights.Length;
                _besideOffsets.AsSpan(right * n, n).CopyTo(offsets);
                return _beside[right];
            }

            return Seam(Left, (long)right * ColumnStride, Right, (long)left * ColumnStride, ColumnLength, offsets);
        }

        private static readonly bool UseVectors = Vector256.IsHardwareAccelerated;

        /// <summary>
        /// Per component: the differences a − b, their median (the best offset) and the mean absolute difference from
        /// it; Y + ½(Cb + Cr). The differences are sorted in place (the sum doesn't depend on the order), with a fixed
        /// sorting network for 8, 16 or 32 samples.
        /// </summary>
        private float Seam(float[] a, long ai, float[] b, long bi, int[] lengths, Span<float> offsets)
        {
            Span<float> d = stackalloc float[64];
            float sum = 0;
            for (int c = 0; c < lengths.Length; c++)
            {
                int len = lengths[c];
                var part = d[..len];
                int i = 0;
                if (UseVectors)
                {
                    for (; i + 8 <= len; i += 8)
                    {
                        var difference = Vector256.Create(new ReadOnlySpan<float>(a, (int)(ai + i), 8)) -
                                         Vector256.Create(new ReadOnlySpan<float>(b, (int)(bi + i), 8));
                        difference.CopyTo(part[i..]);
                    }
                }

                for (; i < len; i++)
                    part[i] = a[ai + i] - b[bi + i];
                SortSmall(part);
                float median = 0.5f * (part[(len - 1) / 2] + part[len / 2]);
                float r = 0;
                i = 0;
                if (UseVectors)
                {
                    var m = Vector256.Create(median);
                    var total = Vector256<float>.Zero;
                    for (; i + 8 <= len; i += 8)
                        total += Vector256.Abs(Vector256.Create((ReadOnlySpan<float>)part.Slice(i, 8)) - m);
                    r = Vector256.Sum(total);
                }

                for (; i < len; i++)
                    r += Math.Abs(part[i] - median);
                offsets[c] = median;
                sum += Weights[c] * r / len;
                ai += len;
                bi += len;
            }

            return sum;
        }

        private static void SortSmall(Span<float> values)
        {
            switch (values.Length)
            {
                case 8:
                    SortByNetwork(values, Network8);
                    break;
                case 16:
                    SortByNetwork(values, Network16);
                    break;
                case 32:
                    SortByNetwork(values, Network32);
                    break;
                default:
                    values.Sort();
                    break;
            }
        }

        /// <summary>Sorts with a list of compare-exchange pairs (index, index), in order.</summary>
        private static void SortByNetwork(Span<float> values, ReadOnlySpan<byte> network)
        {
            for (int k = 0; k < network.Length; k += 2)
            {
                int i = network[k], j = network[k + 1];
                float x = values[i], y = values[j];
                values[i] = Math.Min(x, y);
                values[j] = Math.Max(x, y);
            }
        }

        // Batcher's odd-even merge sort networks.
        private static ReadOnlySpan<byte> Network8 =>
        [
            0, 1, 2, 3, 0, 2, 1, 3, 1, 2, 4, 5, 6, 7, 4, 6, 5, 7, 5, 6, 0, 4, 2, 6, 2, 4, 1, 5, 3, 7, 3, 5, 1, 2, 3,
            4, 5, 6
        ];

        private static ReadOnlySpan<byte> Network16 =>
        [
            0, 1, 2, 3, 0, 2, 1, 3, 1, 2, 4, 5, 6, 7, 4, 6, 5, 7, 5, 6, 0, 4, 2, 6, 2, 4, 1, 5, 3, 7, 3, 5, 1, 2, 3,
            4, 5, 6, 8, 9, 10, 11, 8, 10, 9, 11, 9, 10, 12, 13, 14, 15, 12, 14, 13, 15, 13, 14, 8, 12, 10, 14, 10,
            12, 9, 13, 11, 15, 11, 13, 9, 10, 11, 12, 13, 14, 0, 8, 4, 12, 4, 8, 2, 10, 6, 14, 6, 10, 2, 4, 6, 8,
            10, 12, 1, 9, 5, 13, 5, 9, 3, 11, 7, 15, 7, 11, 3, 5, 7, 9, 11, 13, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
            12, 13, 14
        ];

        private static ReadOnlySpan<byte> Network32 =>
        [
            0, 1, 2, 3, 0, 2, 1, 3, 1, 2, 4, 5, 6, 7, 4, 6, 5, 7, 5, 6, 0, 4, 2, 6, 2, 4, 1, 5, 3, 7, 3, 5, 1, 2, 3,
            4, 5, 6, 8, 9, 10, 11, 8, 10, 9, 11, 9, 10, 12, 13, 14, 15, 12, 14, 13, 15, 13, 14, 8, 12, 10, 14, 10,
            12, 9, 13, 11, 15, 11, 13, 9, 10, 11, 12, 13, 14, 0, 8, 4, 12, 4, 8, 2, 10, 6, 14, 6, 10, 2, 4, 6, 8,
            10, 12, 1, 9, 5, 13, 5, 9, 3, 11, 7, 15, 7, 11, 3, 5, 7, 9, 11, 13, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
            12, 13, 14, 16, 17, 18, 19, 16, 18, 17, 19, 17, 18, 20, 21, 22, 23, 20, 22, 21, 23, 21, 22, 16, 20, 18,
            22, 18, 20, 17, 21, 19, 23, 19, 21, 17, 18, 19, 20, 21, 22, 24, 25, 26, 27, 24, 26, 25, 27, 25, 26, 28,
            29, 30, 31, 28, 30, 29, 31, 29, 30, 24, 28, 26, 30, 26, 28, 25, 29, 27, 31, 27, 29, 25, 26, 27, 28, 29,
            30, 16, 24, 20, 28, 20, 24, 18, 26, 22, 30, 22, 26, 18, 20, 22, 24, 26, 28, 17, 25, 21, 29, 21, 25, 19,
            27, 23, 31, 23, 27, 19, 21, 23, 25, 27, 29, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 0,
            16, 8, 24, 8, 16, 4, 20, 12, 28, 12, 20, 4, 8, 12, 16, 20, 24, 2, 18, 10, 26, 10, 18, 6, 22, 14, 30, 14,
            22, 6, 10, 14, 18, 22, 26, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 1, 17, 9, 25, 9, 17, 5,
            21, 13, 29, 13, 21, 5, 9, 13, 17, 21, 25, 3, 19, 11, 27, 11, 19, 7, 23, 15, 31, 15, 23, 7, 11, 15, 19,
            23, 27, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13,
            14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30
        ];

        public float Size(ReadOnlySpan<float> offsets)
        {
            float s = 0;
            for (int c = 0; c < Weights.Length; c++)
                s += Weights[c] * Math.Abs(offsets[c]);
            return s;
        }

        /// <summary>
        /// How different MCU <paramref name="right"/> is from MCU <paramref name="left"/> as its left neighbour: the
        /// seam plus <see cref="DpRowStartColourShare"/> of the size of the colour offset.
        /// </summary>
        public float NeighbourDifference(int right, int left, Span<float> offsets) =>
            HorizontalSeam(right, left, offsets) + DpRowStartColourShare * Size(offsets);

        /// <summary>Median <see cref="NeighbourDifference"/> of stream neighbours (about 20 000 samples).</summary>
        public float MedianNeighbourDifference()
        {
            Span<float> offsets = stackalloc float[Weights.Length];
            var differences = new List<float>();
            int step = Math.Max(1, Count / 20_000);
            for (int y = 1; y < Count; y += step)
                differences.Add(NeighbourDifference(y, y - 1, offsets));
            if (differences.Count == 0)
                return 1f;
            differences.Sort();
            return differences[differences.Count / 2];
        }

        /// <summary>Median vertical seam of the stream as it is (every 7th MCU against the one a row earlier).</summary>
        public float MedianSeam(int perRow)
        {
            Span<float> offsets = stackalloc float[Weights.Length];
            var seams = new List<float>();
            for (int y = perRow; y < Count; y += 7)
                seams.Add(VerticalSeam(y, y - perRow, offsets));
            if (seams.Count == 0)
                return 1f;
            seams.Sort();
            return Math.Max(DpMinUnit, seams[seams.Count / 2]);
        }
    }

    /// <summary>
    /// One column of DP states (all y − x for one x), for each of the three layers. Each state k holds up to
    /// <see cref="Slots"/> paths with different numbers of removed MCUs, cheapest first, at the indices
    /// k · Slots .. k · Slots + Slots − 1; unused ones cost <see cref="NoPath"/> and come last.
    /// </summary>
    private sealed class DpColumn
    {
        public DpColumn(int size, int slots, int components)
        {
            Slots = slots;
            Components = components;
            int paths = size * slots;
            for (int l = 0; l < 3; l++)
            {
                Cost[l] = new float[paths];
                Node[l] = new int[paths];
                LastPlaced[l] = new int[paths];
                Removed[l] = new int[paths];
                Offsets[l] = new float[paths * components];
                HasOffsets[l] = new bool[paths];
            }
        }

        public int Slots { get; }

        public int Components { get; }

        public float[][] Cost { get; } = new float[3][];

        /// <summary>Last edit record on the path (−1: none).</summary>
        public int[][] Node { get; } = new int[3][];

        /// <summary>Stream index of the MCU at the previous position (−1: a blank).</summary>
        public int[][] LastPlaced { get; } = new int[3][];

        /// <summary>MCUs removed so far on the path.</summary>
        public int[][] Removed { get; } = new int[3][];

        /// <summary>Best colour offsets of the MCU at the previous position.</summary>
        public float[][] Offsets { get; } = new float[3][];

        public bool[][] HasOffsets { get; } = new bool[3][];

        public void Clear()
        {
            for (int l = 0; l < 3; l++)
                Array.Fill(Cost[l], NoPath);
        }

        /// <summary>Copies every path to <paramref name="to"/> (same size).</summary>
        public void CopyTo(DpColumn to)
        {
            for (int l = 0; l < 3; l++)
            {
                Cost[l].CopyTo(to.Cost[l], 0);
                Node[l].CopyTo(to.Node[l], 0);
                LastPlaced[l].CopyTo(to.LastPlaced[l], 0);
                Removed[l].CopyTo(to.Removed[l], 0);
                Offsets[l].CopyTo(to.Offsets[l], 0);
                HasOffsets[l].CopyTo(to.HasOffsets[l], 0);
            }
        }

        /// <summary>
        /// Makes room for a path costing <paramref name="cost"/> with <paramref name="removed"/> removed MCUs in state
        /// <paramref name="k"/> of <paramref name="layer"/>, if it is among the cheapest with different numbers of
        /// removed MCUs (a dearer path with the same number is dropped). Returns its index with cost and number set, or
        /// −1; the caller fills in the rest.
        /// </summary>
        public int Admit(int layer, int k, float cost, int removed)
        {
            var costs = Cost[layer];
            var counts = Removed[layer];
            int start = k * Slots, end = start + Slots;
            for (int i = start; i < end && costs[i] < NoPath; i++)
            {
                if (counts[i] != removed)
                    continue;
                if (cost >= costs[i])
                    return -1;
                for (int j = i; j < end - 1; j++)
                    Move(layer, j + 1, j);
                costs[end - 1] = NoPath;
                break;
            }

            if (cost >= costs[end - 1])
                return -1;
            int at = end - 1;
            while (at > start && costs[at - 1] > cost)
            {
                Move(layer, at - 1, at);
                at--;
            }

            costs[at] = cost;
            counts[at] = removed;
            return at;
        }

        private void Move(int layer, int from, int to)
        {
            Cost[layer][to] = Cost[layer][from];
            Node[layer][to] = Node[layer][from];
            LastPlaced[layer][to] = LastPlaced[layer][from];
            Removed[layer][to] = Removed[layer][from];
            HasOffsets[layer][to] = HasOffsets[layer][from];
            Array.Copy(Offsets[layer], from * Components, Offsets[layer], to * Components, Components);
        }
    }

    private struct DpNode
    {
        public int Position;
        public int Offset;   // y − x after this edit
        public int Parent;

        /// <summary>
        /// An earlier record on the same path (skew-binary jump pointer), so <see cref="DpSearch.Lookup"/> can skip
        /// over many edits at once: a path through a damaged stretch can have dozens of edits within one row.
        /// </summary>
        public int Jump;

        public int Depth;    // records before this one on the path
        public bool Insert;
    }

    /// <summary>
    /// The preview term of the search (see the class remarks): what the preview expects at every position, every stream
    /// MCU's mean levels averaged with its stream neighbours, and per run of the stream and y − x the colour offset that
    /// fits the run best there (the median difference between the preview and the run's MCUs).
    /// </summary>
    private sealed class DpThumbnail
    {
        private readonly ThumbnailReference _reference;
        private readonly int[] _run;         // run of every stream MCU
        private readonly float[] _worse;     // run · size + y − x − offMin: how much worse the run fits there than at its best
        private readonly int _offMin;
        private readonly int _size;

        private DpThumbnail(ThumbnailReference reference, int[] run, float[] worse, int offMin, int size)
        {
            _reference = reference;
            _run = run;
            _worse = worse;
            _offMin = offMin;
            _size = size;
        }

        /// <summary>
        /// Runs: the stream is cut at every row's worth of MCUs and before and after every MCU that is corrupt or holds no
        /// picture data; a piece shorter than <see cref="DpThumbMinRun"/> joins the one before it unless a corrupt MCU
        /// lies between them.
        /// </summary>
        public static DpThumbnail Build(JpegDocument document, ThumbnailReference reference, int streamLength, int offMin,
            int offMax, float[] weights)
        {
            int n = reference.Components;
            int perRow = document.Frame.McusPerRow;
            int positions = reference.Inside.Length;
            var raw = document.McuMeans(streamLength);
            var means = new float[raw.Length];
            for (int y = 0; y < streamLength; y++)
            {
                int before = Math.Max(0, y - 1), after = Math.Min(streamLength - 1, y + 1);
                for (int c = 0; c < n; c++)
                    means[y * n + c] = (raw[before * n + c] + raw[y * n + c] + raw[after * n + c]) / 3;
            }

            var cut = new bool[streamLength + 1];
            var hard = new bool[streamLength + 1];
            cut[0] = hard[0] = true;
            for (int y = 0; y < streamLength; y += perRow)
                cut[y] = true;
            for (int y = 0; y < streamLength; y++)
            {
                var entry = document._mcus[y];
                if (entry.Status == McuStatus.Corrupt || !HasPictureData(entry))
                    cut[y] = hard[y] = cut[y + 1] = hard[y + 1] = true;
            }

            var starts = new List<int>();
            for (int y = 0; y < streamLength; y++)
            {
                if (!cut[y])
                    continue;
                int end = y + 1;
                while (end < streamLength && !cut[end])
                    end++;
                if (starts.Count == 0 || end - y >= DpThumbMinRun || hard[y])
                    starts.Add(y);
            }

            starts.Add(streamLength);
            int runs = starts.Count - 1, size = offMax - offMin + 1, longest = 0;
            var run = new int[streamLength];
            for (int r = 0; r < runs; r++)
            {
                longest = Math.Max(longest, starts[r + 1] - starts[r]);
                for (int y = starts[r]; y < starts[r + 1]; y++)
                    run[y] = r;
            }

            var expected = reference.Expected;
            var inside = reference.Inside;
            int rows = Math.Max(1, (int)Math.Ceiling(1 / (reference.Scale * document.Frame.McuHeight)));
            var worse = new float[(long)runs * size];
            Parallel.For(0, runs, () => (Values: new float[longest], Fit: new float[size], Offset: new float[n]),
                (r, _, scratch) =>
            {
                var (values, fit, offset) = scratch;
                int first = starts[r], end = starts[r + 1], length = end - first;
                for (int k = 0; k < size; k++)
                {
                    int off = k + offMin;
                    for (int c = 0; c < n; c++)
                    {
                        int count = 0;
                        for (int y = first; y < end; y++)
                        {
                            int x = y - off;
                            if (x >= 0 && x < positions && inside[x])
                                values[count++] = expected[x * n + c] - means[y * n + c];
                        }

                        offset[c] = count == 0 ? 0 : Median(values.AsSpan(0, count));
                    }

                    float sum = 0;
                    for (int y = first; y < end; y++)
                    {
                        int x = y - off;
                        if (x < 0 || x >= positions || !inside[x])
                        {
                            sum += DpThumbCap;
                            continue;
                        }

                        float d = 0;
                        for (int c = 0; c < n; c++)
                            d += weights[c] * Math.Abs(expected[x * n + c] - means[y * n + c] - offset[c]);
                        sum += Math.Min(d, DpThumbCap);
                    }

                    fit[k] = sum / length;
                }

                long at = (long)r * size;
                float best = float.MaxValue;
                for (int k = 0; k < size; k++)
                {
                    float f = fit[k];
                    for (int row = 1; row <= rows; row++)
                    {
                        int step = row * perRow;
                        if (k - step >= 0)
                            f = Math.Min(f, fit[k - step]);
                        if (k + step < size)
                            f = Math.Min(f, fit[k + step]);
                    }

                    worse[at + k] = f;
                    best = Math.Min(best, f);
                }

                for (int k = 0; k < size; k++)
                    worse[at + k] -= best;
                return scratch;
            }, _ => { });

            return new DpThumbnail(reference, run, worse, offMin, size);
        }

        /// <summary>Preview cost of placing stream MCU <paramref name="y"/> at position <paramref name="x"/>.</summary>
        public float Place(int x, int y)
        {
            if (!_reference.Inside[x])
                return 0;
            int k = y - x - _offMin;
            if (k < 0 || k >= _size)
                return DpThumbWeight * DpThumbCap;
            return DpThumbWeight * _worse[(long)_run[y] * _size + k];
        }

        /// <summary>Preview cost of a blank at position <paramref name="x"/>.</summary>
        public float Blank(int x) => x < _reference.Inside.Length && _reference.Inside[x] ? DpThumbWeight * DpThumbBlank : 0;
    }

    private sealed class DpSearch(
        DpSamples samples, DpThumbnail? thumbnail, int slotCount, bool backtrack, int perRow, int positions,
        int streamLength, int offMin, int offMax, float unit, Action checkpoint, Action<int, int>? progress)
    {
        private readonly int _size = offMax - offMin + 1;

        /// <summary>First position the search may change: the second row, or the first one with a preview.</summary>
        public int First { get; } = thumbnail == null ? perRow : 0;
        private readonly int _components = samples.Weights.Length;
        private readonly float _cap = DpCap * unit;
        private readonly float _rowStartThreshold = DpRowStartThreshold * unit;
        private readonly float _open = DpOpen * unit;
        private readonly float _removeEach = DpRemoveEach * unit;
        private DpNode[] _nodes = new DpNode[1 << 16];
        private int _nodeCount;

        // Place costs of the column being worked on, per layer and state.
        private float[][] _placeCost = [];
        private float[][] _placeOffsets = [];
        private bool[][] _placeHasOffsets = [];

        private int NewNode(int position, int offset, int parent, bool insert)
        {
            if (_nodeCount == _nodes.Length)
                Array.Resize(ref _nodes, _nodes.Length * 2);
            int jump = parent, depth = 0;
            if (parent >= 0)
            {
                depth = _nodes[parent].Depth + 1;
                int j1 = _nodes[parent].Jump, j2 = j1 >= 0 ? _nodes[j1].Jump : -1;
                if (j2 >= 0 && _nodes[parent].Depth - _nodes[j1].Depth == _nodes[j1].Depth - _nodes[j2].Depth)
                    jump = j2;
            }

            _nodes[_nodeCount] = new DpNode
            {
                Position = position, Offset = offset, Parent = parent, Jump = jump, Depth = depth, Insert = insert,
            };
            return _nodeCount++;
        }

        /// <summary>Stream index of the MCU at position <paramref name="q"/> on the path ending in record <paramref name="node"/>, −1 for a blank.</summary>
        private int Lookup(int node, int q)
        {
            while (node >= 0 && _nodes[node].Position > q)
            {
                int jump = _nodes[node].Jump;
                node = jump >= 0 && _nodes[jump].Position > q ? jump : _nodes[node].Parent;
            }

            if (node < 0)
                return q;
            if (_nodes[node].Insert && _nodes[node].Position == q)
                return -1;
            return q + _nodes[node].Offset;
        }

        /// <summary>Returns the stream index at every position (−1 for a blank); positions before <see cref="First"/> are unchanged.</summary>
        public int[] Run(out double cost)
        {
            int size = _size, slots = slotCount;
            var cur = new DpColumn(size, slots, _components);
            var next = new DpColumn(size, slots, _components);
            cur.Clear();
            next.Clear();
            _placeCost = new float[3][];
            _placeOffsets = new float[3][];
            _placeHasOffsets = new bool[3][];
            for (int l = 0; l < 3; l++)
            {
                _placeCost[l] = new float[size * slots];
                _placeOffsets[l] = new float[size * slots * _components];
                _placeHasOffsets[l] = new bool[size * slots];
            }

            int start = -offMin * slots;
            cur.Cost[LayerPlaced][start] = 0;
            cur.Node[LayerPlaced][start] = -1;
            cur.LastPlaced[LayerPlaced][start] = First - 1;
            cur.Removed[LayerPlaced][start] = 0;
            cur.HasOffsets[LayerPlaced][start] = false;

            int rows = (positions + perRow - 1) / perRow;
            // States at the last few row starts, to go back to (see the class remarks).
            var saved = new DpColumn[DpBacktrackRows + 1];
            var savedAt = new int[saved.Length];
            for (int i = 0; i < saved.Length; i++)
            {
                saved[i] = new DpColumn(size, slots, _components);
                saved[i].Clear();
                savedAt[i] = -1;
            }

            float jumpMin = DpBacktrackJump * samples.MedianNeighbourDifference();
            int backtracks = 0, lastBonus = -1, bonusAt = -1, bonusNext = -1;
            for (int x = First; x <= positions; x++)
            {
                if (x % perRow == 0 && x < positions)
                {
                    int from = x - DpBacktrackRows * perRow;
                    int slot = from / perRow % saved.Length;
                    int edge = backtrack && from >= perRow && from >= lastBonus + DpBacktrackRows * perRow &&
                               backtracks < DpBacktrackMax && savedAt[slot] == from
                        ? FindMisalignment(cur, from, x, jumpMin)
                        : -1;
                    if (edge >= 0)
                    {
                        saved[slot].CopyTo(cur);
                        backtracks++;
                        lastBonus = bonusAt = from;
                        bonusNext = edge;
                        x = from;
                    }

                    slot = x / perRow % saved.Length;
                    cur.CopyTo(saved[slot]);
                    savedAt[slot] = x;
                }

                Removals(cur, x);
                if (x == bonusAt)
                {
                    float bonus = DpBacktrackBonus * unit;
                    for (int k = 0; k < size; k++)
                    {
                        int y = x + k + offMin;
                        if (((y - bonusNext) % perRow + perRow) % perRow != 0)
                            continue;
                        for (int l = 0; l < 3; l++)
                        {
                            for (int i = k * slots; i < (k + 1) * slots; i++)
                            {
                                if (cur.Cost[l][i] < NoPath)
                                    cur.Cost[l][i] -= bonus;
                            }
                        }
                    }

                    bonusAt = -1;
                }

                if (x == positions)
                    break;

                PlaceCosts(cur, x);
                next.Clear();
                Steps(cur, next, x);
                (cur, next) = (next, cur);

                if ((x + 1) % perRow == 0)
                {
                    Compact([cur, .. saved]);
                    checkpoint();
                    progress?.Invoke((x + 1) / perRow, rows);
                }
            }

            int bestNode = -1, bestLayer = -1;
            float best = NoPath;
            for (int l = 0; l < 3; l++)
            {
                for (int i = 0; i < size * slots; i++)
                {
                    if (cur.Cost[l][i] < best)
                    {
                        best = cur.Cost[l][i];
                        bestNode = cur.Node[l][i];
                        bestLayer = l;
                    }
                }
            }

            cost = best;
            var placed = new int[positions];
            for (int i = 0; i < First; i++)
                placed[i] = i;
            for (int i = First; i < positions; i++)
                placed[i] = bestLayer < 0 ? (i < streamLength ? i : -1) : Lookup(bestNode, i);
            return placed;
        }

        /// <summary>
        /// Checks the best path of column <paramref name="x"/> (a row start) over the rows <paramref name="from"/>..x − 1
        /// for rows shifted against the picture's edges (see the class remarks). Returns the stream index of the MCU after
        /// the jump that is taken as the edge, or −1 if the rows look lined up.
        /// </summary>
        private int FindMisalignment(DpColumn col, int from, int x, float jumpMin)
        {
            int node = -1;
            float best = NoPath;
            for (int l = LayerPlaced; l <= LayerInserted; l++)
            {
                for (int i = 0; i < col.Cost[l].Length; i++)
                {
                    if (col.Cost[l][i] < best)
                    {
                        best = col.Cost[l][i];
                        node = col.Node[l][i];
                    }
                }
            }

            if (best >= NoPath)
                return -1;

            // The path from the MCU before the first row on; differences[r * W + c]: column c of row r against its
            // left neighbour (column 0: against the end of the row above).
            int count = x - from;
            var path = new int[count + 1];
            for (int i = 0; i <= count; i++)
                path[i] = Lookup(node, from - 1 + i);
            var differences = new float[count];
            Span<float> offsets = stackalloc float[_components];
            for (int i = 0; i < count; i++)
            {
                int left = path[i], right = path[i + 1];
                if (left >= 0 && right >= 0 && right < streamLength)
                    differences[i] = samples.NeighbourDifference(right, left, offsets);
                else if (i % perRow == 0)
                    return -1;   // a blank at a row start: no edge to judge
            }

            for (int r = 0; r < DpBacktrackRows; r++)
            {
                if (differences[r * perRow] >= _rowStartThreshold)
                    return -1;
            }

            int column = -1;
            float jump = 0;
            for (int c = 1; c < perRow; c++)
            {
                float smallest = NoPath;
                for (int r = 0; r < DpBacktrackRows; r++)
                    smallest = Math.Min(smallest, differences[r * perRow + c]);
                if (smallest > jump)
                {
                    jump = smallest;
                    column = c;
                }
            }

            return column > 0 && jump >= jumpMin ? path[column + 1] : -1;
        }

        /// <summary>Runs of removals within column <paramref name="x"/> (free at the end, after the last position).</summary>
        private void Removals(DpColumn col, int x)
        {
            int slots = col.Slots;
            float open = x == positions ? 0 : _open, each = x == positions ? 0 : _removeEach;
            for (int k = 1; k < _size; k++)
            {
                int y = x + k + offMin;
                if (y > streamLength)
                    break;
                foreach (int source in (ReadOnlySpan<int>)[LayerPlaced, LayerRemoved])
                {
                    float step = source == LayerPlaced ? open : each;
                    for (int i = (k - 1) * slots; i < k * slots; i++)
                    {
                        float c = col.Cost[source][i];
                        if (c >= NoPath)
                            break;
                        int at = col.Admit(LayerRemoved, k, c + step, col.Removed[source][i] + 1);
                        if (at < 0)
                            continue;
                        // Removals at the same position: only the last one matters for lookups.
                        int parent = source == LayerPlaced ? col.Node[source][i] : _nodes[col.Node[source][i]].Parent;
                        col.Node[LayerRemoved][at] = NewNode(x, k + offMin, parent, false);
                        col.LastPlaced[LayerRemoved][at] = col.LastPlaced[source][i];
                        col.HasOffsets[LayerRemoved][at] = col.HasOffsets[source][i];
                        Array.Copy(col.Offsets[source], i * _components, col.Offsets[LayerRemoved], at * _components,
                            _components);
                    }
                }
            }
        }

        /// <summary>Cost of placing the next stream MCU at <paramref name="x"/> after every path of the column (in parallel).</summary>
        private void PlaceCosts(DpColumn col, int x)
        {
            int slots = col.Slots;
            Parallel.For(0, (_size + 15) / 16, chunk =>
            {
                Span<float> offsets = stackalloc float[_components];
                Span<float> leftOffsets = stackalloc float[_components];
                Span<float> change = stackalloc float[_components];
                int from = chunk * 16, to = Math.Min(_size, from + 16);
                for (int k = from; k < to; k++)
                {
                    int y = x + k + offMin;
                    float thumb = thumbnail != null && y >= 0 && y < streamLength ? thumbnail.Place(x, y) : 0;
                    int previousAbove = -2, previousLeft = -2;
                    float seam = 0, left = 0;
                    for (int l = 0; l < 3; l++)
                    {
                        for (int i = k * slots; i < (k + 1) * slots; i++)
                        {
                            _placeCost[l][i] = NoPath;
                            if (col.Cost[l][i] >= NoPath)
                                break;
                            if (y < 0 || y >= streamLength)
                                continue;

                            if (x < perRow)
                            {
                                // First row (only with a preview): nothing above; the left seam and the preview decide.
                                int before = col.LastPlaced[l][i];
                                float firstRow = x > 0 && before >= 0
                                    ? DpLeftSeamWeight * samples.HorizontalSeam(y, before, leftOffsets)
                                    : 0;
                                _placeCost[l][i] = Math.Min(firstRow, _cap) + thumb;
                                _placeHasOffsets[l][i] = false;
                                continue;
                            }

                            int above = Lookup(col.Node[l][i], x - perRow);
                            var placeOffsets = _placeOffsets[l].AsSpan(i * _components, _components);
                            var lastOffsets = col.Offsets[l].AsSpan(i * _components, _components);
                            if (above < 0)
                            {
                                _placeCost[l][i] = _cap + thumb;
                                _placeHasOffsets[l][i] = col.HasOffsets[l][i];
                                lastOffsets.CopyTo(placeOffsets);
                                continue;
                            }

                            if (above != previousAbove)
                            {
                                seam = samples.VerticalSeam(y, above, offsets);
                                previousAbove = above;
                            }

                            float size = samples.Size(offsets), colour = size;
                            if (col.HasOffsets[l][i])
                            {
                                for (int c = 0; c < _components; c++)
                                    change[c] = offsets[c] - lastOffsets[c];
                                colour = Math.Min(colour, samples.Size(change));
                            }

                            float c1 = seam + DpColourWeight * colour;
                            int leftIndex = col.LastPlaced[l][i];
                            bool rowStart = x % perRow == 0;
                            if (leftIndex >= 0 && leftIndex != previousLeft)
                            {
                                left = samples.HorizontalSeam(y, leftIndex, leftOffsets);
                                previousLeft = leftIndex;
                            }

                            if (!rowStart && leftIndex >= 0)
                                c1 += DpLeftSeamWeight * left;
                            c1 = Math.Min(c1, _cap);
                            if (rowStart && leftIndex >= 0)
                            {
                                float difference = left + DpRowStartColourShare * samples.Size(leftOffsets);
                                if (difference < _rowStartThreshold)
                                    c1 += DpRowStartWeight * (_rowStartThreshold - difference);
                            }

                            _placeCost[l][i] = c1 + thumb;
                            _placeHasOffsets[l][i] = true;
                            offsets.CopyTo(placeOffsets);
                        }
                    }
                }
            });
        }

        /// <summary>Inserts (from column x to x + 1, one lower y − x) and placements (same y − x).</summary>
        private void Steps(DpColumn col, DpColumn nextCol, int x)
        {
            int slots = col.Slots;
            float blank = thumbnail?.Blank(x) ?? 0;
            // Inserts make edit records, so they are done one state at a time.
            for (int k = 1; k < _size; k++)
            {
                int off = k + offMin, y = x + off;
                if (y < 0 || y > streamLength)
                    continue;
                foreach (int source in (ReadOnlySpan<int>)[LayerPlaced, LayerInserted])
                {
                    float step = (source == LayerPlaced ? _open : _cap) + blank;
                    for (int i = k * slots; i < (k + 1) * slots; i++)
                    {
                        float c = col.Cost[source][i];
                        if (c >= NoPath)
                            break;
                        int at = nextCol.Admit(LayerInserted, k - 1, c + step, col.Removed[source][i]);
                        if (at < 0)
                            continue;
                        nextCol.Node[LayerInserted][at] = NewNode(x, off - 1, col.Node[source][i], true);
                        nextCol.HasOffsets[LayerInserted][at] = false;
                        nextCol.LastPlaced[LayerInserted][at] = -1;
                    }
                }
            }

            // Placements stay in their state and make no edit records, so the states can be done in parallel.
            Parallel.For(0, (_size + 15) / 16, chunk =>
            {
                int to = Math.Min(_size, chunk * 16 + 16);
                for (int k = chunk * 16; k < to; k++)
                {
                    int y = x + k + offMin;
                    if (y < 0 || y >= streamLength)
                        continue;
                    for (int l = 0; l < 3; l++)
                    {
                        for (int i = k * slots; i < (k + 1) * slots; i++)
                        {
                            if (col.Cost[l][i] >= NoPath)
                                break;
                            float place = _placeCost[l][i];
                            if (place >= NoPath)
                                continue;
                            int at = nextCol.Admit(LayerPlaced, k, col.Cost[l][i] + place, col.Removed[l][i]);
                            if (at < 0)
                                continue;
                            nextCol.Node[LayerPlaced][at] = col.Node[l][i];
                            nextCol.HasOffsets[LayerPlaced][at] = _placeHasOffsets[l][i];
                            nextCol.LastPlaced[LayerPlaced][at] = y;
                            Array.Copy(_placeOffsets[l], i * _components, nextCol.Offsets[LayerPlaced],
                                at * _components, _components);
                        }
                    }
                }
            });
        }

        /// <summary>
        /// The exact cost of a finished layout (stream index per position, −1 for a blank) over the positions
        /// <paramref name="from"/>..<paramref name="to"/> − 1, with the same terms as the search. The state of the path
        /// (previous MCU, its colour offsets) is rebuilt from the few positions before <paramref name="from"/>.
        /// </summary>
        public double LayoutCost(int[] placed, int from, int to)
        {
            from = Math.Max(from, First);
            int start = Math.Max(First, from - 4);
            Span<float> offsets = stackalloc float[_components];
            Span<float> leftOffsets = stackalloc float[_components];
            Span<float> last = stackalloc float[_components];
            Span<float> change = stackalloc float[_components];
            bool hasLast = false;
            int left = First - 1, previous = First - 1, blanksInRun = 0;
            if (start > First)
            {
                left = placed[start - 1];
                for (int x = start - 1; x >= 0; x--)
                {
                    if (placed[x] >= 0)
                    {
                        previous = placed[x];
                        break;
                    }
                }

                for (int x = start - 1; x >= First && placed[x] < 0; x--)
                    blanksInRun++;
            }

            double total = 0;
            for (int x = start; x < to; x++)
            {
                int s = placed[x];
                float c = 0;
                if (s < 0)
                {
                    c = blanksInRun == 0 ? _open : _cap;
                    c += thumbnail?.Blank(x) ?? 0;
                    blanksInRun++;
                    hasLast = false;
                    left = -1;
                }
                else
                {
                    blanksInRun = 0;
                    int removed = s - previous - 1;
                    if (removed > 0)
                        c += _open + _removeEach * (removed - 1);

                    int above = x >= perRow ? placed[x - perRow] : -2;
                    float place;
                    if (above == -2)
                    {
                        // First row (only with a preview): as in the search.
                        place = x > 0 && left >= 0
                            ? Math.Min(_cap, DpLeftSeamWeight * samples.HorizontalSeam(s, left, leftOffsets))
                            : 0;
                        hasLast = false;
                    }
                    else if (above < 0)
                    {
                        place = _cap;
                    }
                    else
                    {
                        place = samples.VerticalSeam(s, above, offsets);
                        float colour = samples.Size(offsets);
                        if (hasLast)
                        {
                            for (int k = 0; k < _components; k++)
                                change[k] = offsets[k] - last[k];
                            colour = Math.Min(colour, samples.Size(change));
                        }

                        place += DpColourWeight * colour;
                        bool rowStart = x % perRow == 0;
                        float leftSeam = left >= 0 ? samples.HorizontalSeam(s, left, leftOffsets) : 0;
                        if (!rowStart && left >= 0)
                            place += DpLeftSeamWeight * leftSeam;
                        place = Math.Min(place, _cap);
                        if (rowStart && left >= 0)
                        {
                            float difference = leftSeam + DpRowStartColourShare * samples.Size(leftOffsets);
                            if (difference < _rowStartThreshold)
                                place += DpRowStartWeight * (_rowStartThreshold - difference);
                        }

                        offsets.CopyTo(last);
                        hasLast = true;
                    }

                    c += place;
                    if (thumbnail != null)
                        c += thumbnail.Place(x, s);
                    left = s;
                    previous = s;
                }

                if (x >= from)
                    total += c;
            }

            return total;
        }

        /// <summary>
        /// Drops edit patterns that a row repeats from the row above (see the class remarks): rows with edits whose
        /// start is exactly one row after the start of the row above and one row before the start of the row below
        /// (their edits cancel out within the row). For each run of such rows the layout without their edits (each row
        /// read straight on from its start) is scored with <see cref="LayoutCost"/> over the run and the row after it;
        /// the run with the biggest gain is dropped, and this is repeated while anything gains.
        /// </summary>
        public int[] DropRepeatedPatterns(int[] placed)
        {
            int rows = positions / perRow;
            for (int round = 0; round < DpPatternRounds; round++)
            {
                var start = new int[rows];
                var edited = new bool[rows];
                for (int r = 0; r < rows; r++)
                {
                    start[r] = -1;
                    int previous = -1;
                    for (int x = r * perRow; x < (r + 1) * perRow; x++)
                    {
                        int s = placed[x];
                        if (s < 0)
                        {
                            edited[r] = true;
                            continue;
                        }

                        if (start[r] < 0)
                            start[r] = s;
                        else if (s != previous + 1)
                            edited[r] = true;
                        previous = s;
                    }
                }

                bool InPattern(int r) =>
                    r >= 1 && r + 1 < rows && edited[r] && start[r] >= 0 && start[r - 1] >= 0 && start[r + 1] >= 0 &&
                    start[r] - start[r - 1] == perRow && start[r + 1] - start[r] == perRow;

                int[]? best = null;
                double bestGain = 0;
                for (int r0 = 1; r0 + 1 < rows; r0++)
                {
                    if (!InPattern(r0) || InPattern(r0 - 1))
                        continue;
                    int r1 = r0;
                    while (InPattern(r1 + 1))
                        r1++;

                    var candidate = (int[])placed.Clone();
                    for (int r = r0; r <= r1; r++)
                    {
                        for (int c = 0; c < perRow; c++)
                        {
                            int s = start[r] + c;
                            candidate[r * perRow + c] = s < streamLength ? s : -1;
                        }
                    }

                    int to = Math.Min(positions, (r1 + 2) * perRow);
                    double gain = LayoutCost(placed, r0 * perRow, to) - LayoutCost(candidate, r0 * perRow, to);
                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        best = candidate;
                    }
                }

                if (best == null)
                    break;
                placed = best;
            }

            return placed;
        }

        /// <summary>
        /// Keeps only the edit records the paths in <paramref name="cols"/> can still reach, each path completely (the
        /// final layout is read from them).
        /// </summary>
        private void Compact(DpColumn[] cols)
        {
            var map = new int[_nodeCount];
            Array.Fill(map, -2);
            foreach (var col in cols)
            {
                for (int l = 0; l < 3; l++)
                {
                    for (int i = 0; i < col.Cost[l].Length; i++)
                    {
                        int node = col.Cost[l][i] < NoPath ? col.Node[l][i] : -1;
                        while (node >= 0 && map[node] == -2)
                        {
                            map[node] = -1;
                            node = _nodes[node].Parent;
                        }
                    }
                }
            }

            int kept = 0;
            for (int i = 0; i < _nodeCount; i++)
            {
                if (map[i] == -1)
                    map[i] = kept++;
            }

            var nodes = new DpNode[Math.Max(1 << 16, kept * 2)];
            for (int i = 0; i < _nodeCount; i++)
            {
                if (map[i] < 0)
                    continue;
                var node = _nodes[i];
                node.Parent = node.Parent >= 0 ? map[node.Parent] : -1;
                node.Jump = node.Jump >= 0 && map[node.Jump] >= 0 ? map[node.Jump] : node.Parent;
                nodes[map[i]] = node;
            }

            foreach (var col in cols)
            {
                for (int l = 0; l < 3; l++)
                {
                    for (int i = 0; i < col.Cost[l].Length; i++)
                    {
                        if (col.Cost[l][i] < NoPath && col.Node[l][i] >= 0)
                            col.Node[l][i] = map[col.Node[l][i]];
                    }
                }
            }

            _nodes = nodes;
            _nodeCount = kept;
        }
    }
}
