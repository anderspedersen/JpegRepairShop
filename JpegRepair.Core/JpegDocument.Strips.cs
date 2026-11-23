using System.Collections.Concurrent;
using System.Diagnostics;

namespace JpegRepair.Core;

/// <summary>
/// A fix for one stripe of the MCU stream, found by <see cref="JpegDocument.FindStripFixes"/> and applied by
/// <see cref="JpegDocument.ApplyStripFix"/>.
/// </summary>
public sealed class StripFix
{
    internal StripFix(int first, int last, bool badStripe, int regionFirst, McuEntry[] regionEntries, int shift,
        int insertAt, int removeAt, int colourFirst, int colourLast, int[] deltas, double costBefore, double costAfter)
    {
        First = first;
        Last = last;
        IsBadStripe = badStripe;
        RegionFirst = regionFirst;
        RegionEntries = regionEntries;
        Shift = shift;
        InsertAt = insertAt;
        RemoveAt = removeAt;
        ColourFirst = colourFirst;
        ColourLast = colourLast;
        DeltaArray = deltas;
        CostBefore = costBefore;
        CostAfter = costAfter;
    }

    /// <summary>First and last stream position of the stripe the fix is for (before the fix).</summary>
    public int First { get; }

    public int Last { get; }

    /// <summary>True for a colour fix of a stripe of bad MCUs, false for a stripe of good MCUs.</summary>
    public bool IsBadStripe { get; }

    /// <summary>The positions the fix changes (from the first edit point to the last).</summary>
    public int RegionFirst { get; }

    public int RegionLast => RegionFirst + RegionEntries.Length - 1;

    /// <summary>The MCUs in the region when the fix was found: it only fits while they are still there.</summary>
    internal McuEntry[] RegionEntries { get; }

    /// <summary>
    /// How many MCUs the part between the edit points moves: positive = right (blanks inserted before it, as many MCUs
    /// removed after it), negative = left, 0 = not moved.
    /// </summary>
    public int Shift { get; }

    /// <summary>
    /// Where the |<see cref="Shift"/>| blanks are inserted and where the |Shift| removed MCUs start, both −1 when nothing
    /// moves. Applied in order: for a move right the insert first (the remove position counts the inserted blanks), for a
    /// move left the remove first.
    /// </summary>
    public int InsertAt { get; }

    public int RemoveAt { get; }

    /// <summary>Positions (after any move) whose DC is changed by <see cref="Deltas"/>; −1 if the colours are left alone.</summary>
    public int ColourFirst { get; }

    public int ColourLast { get; }

    /// <summary>DC change per component, in quantization steps.</summary>
    public IReadOnlyList<int> Deltas => DeltaArray;

    internal int[] DeltaArray { get; }

    /// <summary>Mean difference (levels per sample) across the seams with the row above, before and after.</summary>
    public double CostBefore { get; }

    public double CostAfter { get; }

    /// <summary>Identifies the fix, so the same fix isn't applied twice.</summary>
    public string Key => $"{First}:{Last}:{Shift}:{InsertAt}:{RemoveAt}:{string.Join(",", DeltaArray)}";
}

/// <summary>
/// The last step of "Fix all": stripes of the MCU stream. Read in stream order (left to right, row after row, ignoring
/// where rows end), the MCUs fall into alternating stripes of good MCUs and bad MCUs (flagged by the sensitive
/// detection, <see cref="FindBadMcusSensitive"/>, or not decoded without errors). A small burst of garbage in the data
/// often leaves the good stripe after it a few MCUs out of place or with a wrong DC (colour/brightness) until the next
/// burst; these stripes are too short for the misalignment and colour search of the rest of "Fix all".
/// </summary>
public sealed partial class JpegDocument
{
    /// <summary>Stripes longer than this share of a row are left to the rest of "Fix all".</summary>
    private const double StripMaxRowShare = 1.0;

    /// <summary>
    /// Shortest part that is fixed; shorter ones are too short to judge. For a move, the part is everything that moves
    /// (the good stripe plus the bad MCUs between it and the edit points), so a short good stripe can still be moved
    /// together with bad MCUs next to it.
    /// </summary>
    private const int StripMinLength = 3;

    /// <summary>A fix must lower the seam difference by at least this share...</summary>
    private const double StripMinGainShare = 0.3;

    /// <summary>
    /// ...or this share for a colour fix of a bad stripe: there only a clear win shows that the stripe has a wrong colour
    /// and is not garbage (whose seams a DC change also improves a little).
    /// </summary>
    private const double FlaggedRunMinGainShare = 0.5;

    /// <summary>...and by at least this many levels per sample.</summary>
    private const double StripMinGainLevels = 1.5;

    /// <summary>A colour correction on its own (no move) must change some component by at least this many levels.</summary>
    private const double StripMinColourLevels = 10;

    /// <summary>A move must bring the seam difference to at most this share of correcting the same MCUs where they are.</summary>
    private const double StripMoveFactor = 0.8;

    /// <summary>
    /// The part is moved by at most this share of a row (in MCUs), and never by more than the bad stripe it takes MCUs
    /// from.
    /// </summary>
    private const double StripMaxShiftShare = 0.5;

    /// <summary>...and by at most this many times the length of the good stripe.</summary>
    private const int StripMaxShiftPerLength = 5;

    /// <summary>
    /// A cached stripe result stays valid while no MCU row from this many rows above the MCUs it looked at to this many
    /// below has changed: the scoring compares with the row above, whether that counts as good depends on the detection,
    /// which looks at its neighbours in turn, and so does the detection that splits the stream into stripes.
    /// </summary>
    private const int StripCacheRowMargin = 2;

    // Change tracking for the stripe cache: every redraw stamps its rows with a new version (RenderFrom), every mark
    // change bumps _marksVersion.
    private long _changeVersion;
    private long[]? _rowVersion;
    private long _marksVersion;

    /// <summary>
    /// What a stripe's evaluation depends on besides the picture around it: its place and kind, the bad stripes next to
    /// it, and how far a move can go into each (the shift limit, capped by their lengths).
    /// </summary>
    private readonly record struct StripCacheKey(int First, int Last, bool Good, int BeforeFirst, int AfterLast, int Left, int Right);

    /// <summary>Result of each stripe evaluation (fix or none), with the versions it was worked out at.</summary>
    private readonly ConcurrentDictionary<StripCacheKey, (long Version, long Marks, StripFix? Fix)> _stripCache = new();

    /// <summary>Records that MCU rows <paramref name="firstRow"/>..<paramref name="lastRow"/> changed (from RenderFrom).</summary>
    private void NoteRowsChanged(int firstRow, int lastRow)
    {
        var rows = _rowVersion ??= new long[Frame.McuRows];
        long version = ++_changeVersion;
        for (int r = Math.Max(0, firstRow); r <= Math.Min(lastRow, rows.Length - 1); r++)
            rows[r] = version;
    }

    /// <summary>True if no row around MCUs <paramref name="firstMcu"/>..<paramref name="lastMcu"/> changed after <paramref name="version"/>.</summary>
    private bool RowsUnchangedSince(long version, int firstMcu, int lastMcu)
    {
        if (_rowVersion is not { } rows)
            return true;
        int perRow = Frame.McusPerRow;
        int r0 = Math.Max(0, firstMcu / perRow - StripCacheRowMargin);
        int r1 = Math.Min(rows.Length - 1, lastMcu / perRow + StripCacheRowMargin);
        for (int r = r0; r <= r1; r++)
        {
            if (rows[r] > version)
                return false;
        }

        return true;
    }

    /// <summary>Longest move (in MCUs) a stripe fix may make in this picture (<see cref="StripMaxShiftShare"/> of a row).</summary>
    public int StripMaxShift => Math.Max(1, (int)(Frame.McusPerRow * StripMaxShiftShare));

    /// <summary>Each MCU of a move beyond the first must make the seams this much better (share), so short stripes don't jump.</summary>
    private const double StripShiftPenalty = 0.05;

    /// <summary>
    /// For every stripe of the stream (see the class summary), the best fix, if one improves its seams with the row
    /// above enough; ordered by how much they improve, most first. Each is found with the picture as it is now, so after
    /// applying one, check <see cref="CanApplyStripFix"/> before applying the next. "Fix all" uses
    /// <see cref="FindNextStripFix"/> instead.
    /// </summary>
    public IReadOnlyList<StripFix> FindStripFixes(IReadOnlySet<string>? done = null, CancellationToken cancellation = default)
    {
        var search = new StripSearch(this);
        var found = new List<StripFix>();
        for (int k = 0; k < search.Stripes.Count; k++)
        {
            cancellation.ThrowIfCancellationRequested();
            Checkpoint();
            if (search.Evaluate(k) is { } fix && (done == null || !done.Contains(fix.Key)))
                found.Add(fix);
        }

        return found.OrderByDescending(f => f.CostBefore - f.CostAfter).ToList();
    }

    /// <summary>The fix from <see cref="FindStripFixes"/> that improves most, or null.</summary>
    public StripFix? FindStripFix(IReadOnlySet<string>? done = null, CancellationToken cancellation = default) =>
        FindStripFixes(done, cancellation).FirstOrDefault();

    /// <summary>
    /// The first stripe fix in stream order (top to bottom), starting with the stripe that holds stream position
    /// <paramref name="from"/>, or null if none from there on needs one. "Fix all" makes one pass: it applies each fix at
    /// once and goes on from <see cref="StripFix.RegionLast"/> + 1, so the stripes below are judged against the fixed
    /// rows above them. Starting with the stripe that holds that position (not the next one that starts there) takes in
    /// a stripe that the fix merged with the MCUs after it, e.g. displaced bad MCUs that became good; the next fix always
    /// ends at or after <paramref name="from"/>, so the pass always moves on.
    ///
    /// <para>Parts shorter than <see cref="StripMinLength"/> MCUs are never fixed: no colour correction of a good
    /// or bad stripe that short, and no move whose moving part (good stripe plus the bad MCUs between it and the edit
    /// points) is that short. A short good stripe can still move together with bad MCUs next to it.</para>
    ///
    /// <para><b>Good stripes</b> (at most <see cref="StripMaxRowShare"/> of a row long): the stripe where it is, with its
    /// colour corrected as a whole; and moves by up to <see cref="StripMaxShiftShare"/> of a row, and at most
    /// <see cref="StripMaxShiftPerLength"/> × the stripe's length, left or right, which need a bad stripe on both sides. A move inserts blanks at a
    /// point in one bad stripe and removes as many MCUs at a point in the other, so nothing outside those points moves.
    /// Every point in one bad stripe is tried with the first or last of those in the other, both ways. Everything between
    /// the two points moves together: the good stripe and the bad MCUs between it and the points, which may only have
    /// been displaced. That whole part is scored in its new place and gets one colour correction. A move must clearly
    /// beat correcting the same part's colour where it is.</para>
    ///
    /// <para><b>Bad stripes</b> (same length limit): colour only, as a whole. The sensitive detection flags a
    /// whole stretch with a strongly wrong colour, leaving no good stripe in it.</para>
    ///
    /// <para>Inserted (blank) and corrupt MCUs are left out of all scoring and colour estimates, but they don't stop a
    /// stripe from being fixed: a move takes them along, and a colour correction covers the whole stripe (blanks keep
    /// their grey).</para>
    ///
    /// <para><b>Scoring</b> compares only with the row above (which, working top to bottom, has been fixed already):
    /// the seam differences along the top edge, and per component the colour offset from them.</para>
    ///
    /// Changes nothing; can run on a worker thread as long as the document is not edited meanwhile.
    /// </summary>
    public StripFix? FindNextStripFix(int from, IReadOnlySet<string>? done = null, CancellationToken cancellation = default)
    {
        var search = new StripSearch(this);
        for (int k = 0; k < search.Stripes.Count; k++)
        {
            if (search.Stripes[k].Last < from)
                continue;
            cancellation.ThrowIfCancellationRequested();
            Checkpoint();
            if (search.Evaluate(k) is { } fix && (done == null || !done.Contains(fix.Key)))
                return fix;
        }

        return null;
    }

    /// <summary>
    /// The stripes of the stream as the picture is now (from the sensitive detection, done once), and the evaluation of
    /// each one, with results taken from the document's cache where nothing around a stripe has changed.
    /// </summary>
    private sealed class StripSearch
    {
        private readonly JpegDocument _doc;
        private readonly BadMcuMap _bad;
        private readonly int _count, _maxLength, _maxShift;
        private readonly long _version, _marksVersion;
        private readonly ConcurrentDictionary<McuEntry, float[]> _samples = new(ReferenceEqualityComparer.Instance);

        public StripSearch(JpegDocument doc)
        {
            _doc = doc;
            var frame = doc.Frame;
            _count = Math.Min(doc._mcus.Count, frame.McuCount);
            using (PerfLog.Time("Strips: bad-MCU detection (sensitive)"))
                _bad = doc.FindBadMcusSensitive();

            using (PerfLog.Time("Strips: partition into stripes"))
            {
                var good = new bool[_count];
                for (int i = 0; i < _count; i++)
                    good[i] = doc.CountsAsDecoded(doc._mcus[i]) && !_bad.IsBad(i);
                for (int s = 0; s < _count;)
                {
                    int e = s;
                    while (e + 1 < _count && good[e + 1] == good[s])
                        e++;
                    Stripes.Add((s, e, good[s]));
                    s = e + 1;
                }
            }

            _maxLength = Math.Max(1, (int)(frame.McusPerRow * StripMaxRowShare));
            _maxShift = doc.StripMaxShift;
            _version = doc._changeVersion;
            _marksVersion = doc._marksVersion;
        }

        public List<(int First, int Last, bool Good)> Stripes { get; } = [];

        /// <summary>An MCU the scoring may compare with: decoded (or filled) and not flagged.</summary>
        private bool Reference(int i) =>
            i >= 0 && i < _count && (_doc.CountsAsDecoded(_doc._mcus[i]) || _doc._mcus[i].Status == McuStatus.Filled) &&
            !_bad.IsBad(i);

        /// <summary>The fix for stripe <paramref name="k"/>, or null (also if it is too long or too short to fix).</summary>
        public StripFix? Evaluate(int k)
        {
            var (first, last, isGood) = Stripes[k];
            int length = last - first + 1;
            if (length > _maxLength || !isGood && length < StripMinLength)
                return null;

            // A stripe whose surroundings haven't changed since it was last evaluated (with the same reach for moves)
            // gets the same result: take it from the cache.
            (int First, int Last)? before = isGood && k > 0 ? (Stripes[k - 1].First, Stripes[k - 1].Last) : null;
            (int First, int Last)? after = isGood && k + 1 < Stripes.Count ? (Stripes[k + 1].First, Stripes[k + 1].Last) : null;
            int maxShift = isGood ? Math.Min(_maxShift, StripMaxShiftPerLength * length) : 0;
            var key = new StripCacheKey(first, last, isGood, before?.First ?? -1, after?.Last ?? -1,
                before is { } near1 ? Math.Min(maxShift, near1.Last - near1.First + 1) : 0,
                after is { } near2 ? Math.Min(maxShift, near2.Last - near2.First + 1) : 0);
            int spanFirst = before?.First ?? first, spanLast = after?.Last ?? last;
            if (_doc._stripCache.TryGetValue(key, out var cached) && cached.Marks == _marksVersion &&
                _doc.RowsUnchangedSince(cached.Version, spanFirst, spanLast))
            {
                PerfLog.Count("Strips: stripes taken from the cache", 1);
                return cached.Fix;
            }

            long start = PerfLog.Enabled ? Stopwatch.GetTimestamp() : 0;
            var scorer = new StripScorer(_doc, Reference, _samples);
            var fix = isGood
                ? _doc.EvaluateGoodStripe(scorer, first, last, before, after, maxShift)
                : _doc.EvaluateBadStripe(scorer, first, last);
            _doc._stripCache[key] = (_version, _marksVersion, fix);

            if (PerfLog.Enabled)
            {
                var time = Stopwatch.GetElapsedTime(start);
                PerfLog.Add(isGood ? "Strips: evaluate good stripe" : "Strips: evaluate bad stripe", time);
                PerfLog.Count("Strips: Fit calls", scorer.FitCalls);
                PerfLog.Count("Strips: seam computations", scorer.SeamComputations);
                if (time.TotalMilliseconds >= StripSlowMs)
                {
                    int lengthBefore = before is { } b1 ? b1.Last - b1.First + 1 : 0;
                    int lengthAfter = after is { } b2 ? b2.Last - b2.First + 1 : 0;
                    PerfLog.Line($"    slow: {(isGood ? "good" : "bad")} stripe MCUs {first}–{last} (length {length}, " +
                                 $"bad before {lengthBefore}, after {lengthAfter}, shift limit {maxShift}): " +
                                 $"{time.TotalMilliseconds:0} ms, {scorer.FitCalls:N0} fits, {scorer.SeamComputations:N0} seams");
                }
            }

            return fix;
        }
    }

    /// <summary>Stripe evaluations taking at least this long (ms) get a line of their own in the performance log.</summary>
    private const double StripSlowMs = 50;

    /// <summary>True if the MCUs the fix changes are still the ones it was found for.</summary>
    public bool CanApplyStripFix(StripFix fix)
    {
        if (fix.RegionLast >= _mcus.Count)
            return false;
        for (int k = 0; k < fix.RegionEntries.Length; k++)
        {
            if (!ReferenceEquals(_mcus[fix.RegionFirst + k], fix.RegionEntries[k]))
                return false;
        }

        return true;
    }

    /// <summary>Applies a fix from <see cref="FindStripFixes"/> as one undo step.</summary>
    /// <exception cref="InvalidOperationException">The MCUs the fix changes have changed since it was found.</exception>
    public void ApplyStripFix(StripFix fix)
    {
        if (!CanApplyStripFix(fix))
            throw new InvalidOperationException("The picture was changed where the stripe fix applies.");

        var parts = new List<Edit>(3);
        void Do(Edit edit)
        {
            edit.Apply(this);
            parts.Add(edit);
        }

        int moved = Math.Abs(fix.Shift);
        if (fix.Shift > 0)
        {
            Do(InsertBlankEdit(fix.InsertAt, moved));
            Do(new RemoveEdit(fix.RemoveAt, moved));
        }
        else if (fix.Shift < 0)
        {
            Do(new RemoveEdit(fix.RemoveAt, moved));
            Do(InsertBlankEdit(fix.InsertAt, moved));
        }

        if (fix.ColourFirst >= 0 && fix.DeltaArray.Any(d => d != 0))
            Do(new RangeDcEdit(fix.ColourFirst, fix.ColourLast, fix.DeltaArray));

        if (parts.Count > 0)
            Record(new RegionEdit(DescribeStripFix(fix), parts.ToArray(), fix.RegionFirst, fix.RegionLast));
    }

    /// <summary>Short description of a stripe fix, e.g. for the undo history.</summary>
    public string DescribeStripFix(StripFix fix)
    {
        string where = fix.First == fix.Last ? $"MCU {fix.First}" : $"MCUs {fix.First}–{fix.Last}";
        var parts = new List<string>(2);
        if (fix.Shift != 0)
        {
            // Positions as they are before the fix.
            int k = Math.Abs(fix.Shift);
            int insertBefore = fix.Shift > 0 ? fix.InsertAt : fix.InsertAt + k;
            int removedFirst = fix.Shift > 0 ? fix.RemoveAt - k : fix.RemoveAt;
            string blanks = k == 1 ? "a blank" : $"{k} blanks";
            string removed = k == 1 ? $"MCU {removedFirst}" : $"MCUs {removedFirst}–{removedFirst + k - 1}";
            parts.Add($"moved {(fix.Shift > 0 ? "right" : "left")} by {k} ({blanks} inserted before MCU {insertBefore}, {removed} removed)");
        }

        if (fix.ColourFirst >= 0 && fix.DeltaArray.Any(d => d != 0))
        {
            string range = fix.ColourFirst == fix.ColourLast ? $"MCU {fix.ColourFirst}" : $"MCUs {fix.ColourFirst}–{fix.ColourLast}";
            parts.Add($"colours of {range} ({string.Join(", ", fix.DeltaArray.Select(d => d.ToString("+0;-0;0")))})");
        }

        return $"Fine-tune {where}: {string.Join(", ", parts)}";
    }

    private McuEntry[] EntriesAt(int first, int last)
    {
        var entries = new McuEntry[last - first + 1];
        for (int k = 0; k < entries.Length; k++)
            entries[k] = _mcus[first + k];
        return entries;
    }

    /// <summary>True if the deltas change some component by at least <see cref="StripMinColourLevels"/>.</summary>
    private bool IsVisibleColourChange(int[] deltas)
    {
        for (int c = 0; c < deltas.Length; c++)
        {
            if (Math.Abs(deltas[c] * _quant[c][0] / 8f) >= StripMinColourLevels)
                return true;
        }

        return false;
    }

    /// <summary>Colour fix of a whole bad stripe at positions first..last, or null.</summary>
    private StripFix? EvaluateBadStripe(StripScorer scorer, int first, int last)
    {
        var entries = EntriesAt(first, last);
        if (scorer.Fit(entries, first) is not { } fit || !IsVisibleColourChange(fit.Deltas))
            return null;
        double gain = fit.CostWithout - fit.CostWith;
        if (gain < Math.Max(StripMinGainLevels, FlaggedRunMinGainShare * fit.CostWithout))
            return null;
        return new StripFix(first, last, true, first, entries, 0, -1, -1, first, last, fit.Deltas, fit.CostWithout, fit.CostWith);
    }

    /// <summary>
    /// Best fix of the good stripe at positions g0..g1 with the bad stripes <paramref name="before"/> and
    /// <paramref name="after"/> next to it (null at the ends of the stream), or null. See <see cref="FindStripFixes"/>.
    /// </summary>
    private StripFix? EvaluateGoodStripe(StripScorer scorer, int g0, int g1, (int First, int Last)? before, (int First, int Last)? after,
        int maxShift)
    {
        var stripe = EntriesAt(g0, g1);
        if (scorer.Fit(stripe, g0) is not { } stay)
            return null;

        // Staying put, with the stripe's colour corrected as a whole (if that is a visible change and the stripe is not
        // too short to judge).
        bool colourStay = g1 - g0 + 1 >= StripMinLength && IsVisibleColourChange(stay.Deltas);
        var bestDeltas = colourStay ? stay.Deltas : new int[stay.Deltas.Length];
        double bestBefore = stay.CostWithout;
        double bestAfter = colourStay ? stay.CostWith : stay.CostWithout;
        double bestShare = bestBefore > 0 ? (bestBefore - bestAfter) / bestBefore : 0;
        int bestShift = 0, bestInsert = -1, bestRemove = -1, bestS0 = g0, bestS1 = g1;

        if (before is { } b1 && after is { } b2)
        {
            // Every point of each bad stripe.
            int nearBefore = b1.First;
            int nearAfter = b2.Last + 1;
            
            for (int t = -Math.Min(maxShift, g0 - b1.First); t <= Math.Min(maxShift, b2.Last - g1); t++)
            {
                Checkpoint();
                if (t == 0)
                    continue;
                int k = Math.Abs(t);

                // Right by k: blanks inserted before x (x in [nearBefore, g0]); the k MCUs from y (in the bad stripe
                // after, y in [g1 + 1, b2.Last − k + 1]) removed. The part x..y−1 moves right.
                // Left by k: the k MCUs from x removed (x in [nearBefore, g0 − k]); blanks inserted before y (in
                // [g1 + 1, b2.Last + 1]). The part x+k..y−1 moves left.
                int xLo = nearBefore, xHi = t > 0 ? g0 : g0 - k;
                int yLo = g1 + 1, yHi = t > 0 ? Math.Min(b2.Last - k + 1, nearAfter) : Math.Min(b2.Last + 1, nearAfter);
                if (xHi < xLo || yHi < yLo)
                    continue;

                var points = new HashSet<(int X, int Y)>();
                for (int x = xLo; x <= xHi; x++)
                {
                    points.Add((x, yLo));
                    points.Add((x, yHi));
                }

                for (int y = yLo; y <= yHi; y++)
                {
                    points.Add((xLo, y));
                    points.Add((xHi, y));
                }

                foreach (var (x, y) in points)
                {
                    Checkpoint();
                    int s0 = t > 0 ? x : x + k, s1 = y - 1;
                    if (s1 - s0 + 1 < StripMinLength)
                        continue;
                    var part = EntriesAt(s0, s1);
                    if (scorer.Fit(part, s0 + t) is not { } moved || scorer.Fit(part, s0) is not { } inPlace)
                        continue;
                    if (moved.CostWith * (1 + StripShiftPenalty * (k - 1)) >= StripMoveFactor * inPlace.CostWith)
                        continue;
                    double share = inPlace.CostWithout > 0 ? (inPlace.CostWithout - moved.CostWith) / inPlace.CostWithout : 0;
                    if (share <= bestShare)
                        continue;

                    bestShare = share;
                    bestBefore = inPlace.CostWithout;
                    bestAfter = moved.CostWith;
                    bestDeltas = moved.Deltas;
                    bestShift = t;
                    bestS0 = s0;
                    bestS1 = s1;
                    bestInsert = t > 0 ? x : y - k;
                    bestRemove = t > 0 ? y + k : x;
                }
            }
        }

        if (bestShift == 0 && bestDeltas.All(d => d == 0))
            return null;
        if (bestBefore - bestAfter < Math.Max(StripMinGainLevels, StripMinGainShare * bestBefore))
            return null;

        // The region: from the first edit point to the last (or the stripe itself).
        int regionFirst, regionLast;
        if (bestShift > 0)
        {
            regionFirst = bestInsert;
            regionLast = bestS1 + bestShift;   // last removed MCU, counted before the insert
        }
        else if (bestShift < 0)
        {
            regionFirst = bestRemove;
            regionLast = bestS1;
        }
        else
        {
            regionFirst = g0;
            regionLast = g1;
        }

        int colourFirst = -1, colourLast = -1;
        if (bestDeltas.Any(d => d != 0))
        {
            colourFirst = bestS0 + bestShift;
            colourLast = bestS1 + bestShift;
        }

        return new StripFix(g0, g1, false, regionFirst, EntriesAt(regionFirst, regionLast), bestShift, bestInsert, bestRemove,
            colourFirst, colourLast, bestDeltas, bestBefore, bestAfter);
    }

    /// <summary>
    /// Seam differences of MCUs placed at given stream positions against the row above (only where that is a reference
    /// MCU: intact and not flagged; working top to bottom, the row above has been fixed already). Edges are extrapolated from the two sample rows next to them, as in
    /// <see cref="EstimateDcShift(int)"/>, so smooth gradients don't count. Sample planes are shared between scorers
    /// (thread-safe); seam lists are cached per scorer.
    /// </summary>
    private sealed class StripScorer(JpegDocument document, Func<int, bool> reference, ConcurrentDictionary<McuEntry, float[]> samples)
    {
        private readonly Dictionary<(McuEntry, int), Seams[]> _seams = new();

        /// <summary>Calls of <see cref="Fit"/> and seam lists worked out (not cached), for the performance log.</summary>
        public int FitCalls, SeamComputations;

        public sealed record FitResult(int[] Deltas, double CostWithout, double CostWith);

        /// <summary>Seam differences of one component, against the row above.</summary>
        private sealed class Seams
        {
            public readonly List<(float Difference, float Roughness)> Above = [];

            public int Count => Above.Count;

            public IEnumerable<(float Difference, float Roughness)> All => Above;
        }

        private float[] Samples(McuEntry entry) => samples.GetOrAdd(entry, e =>
        {
            var planes = new float[document._samplesPerMcu];
            document.EntrySamples(e, planes);
            return planes;
        });

        /// <summary>
        /// Colour correction and seam difference for <paramref name="entries"/> placed from <paramref name="firstPosition"/>
        /// on, as one piece: per component, the correction is the median difference against the row above over the
        /// smoother half of the seam columns, in DC steps. Also returns the weighted mean difference without and with the correction. Null if
        /// no seam can be compared.
        /// </summary>
        public FitResult? Fit(IReadOnlyList<McuEntry> entries, int firstPosition)
        {
            FitCalls++;
            int components = document.Frame.Components.Count;
            var merged = new Seams[components];
            for (int c = 0; c < components; c++)
                merged[c] = new Seams();
            for (int k = 0; k < entries.Count; k++)
            {
                if (!Scored(entries[k]))
                    continue;
                var one = SeamsAt(entries[k], firstPosition + k);
                for (int c = 0; c < components; c++)
                {
                    merged[c].Above.AddRange(one[c].Above);
                }
            }

            if (merged.All(s => s.Count == 0))
                return null;

            var deltas = new int[components];
            for (int c = 0; c < components; c++)
            {
                if (SmoothMedian(merged[c].Above) is { } above)
                    deltas[c] = (int)MathF.Round(above / Step(c));
            }

            return new FitResult(deltas, Cost(merged, new int[components]), Cost(merged, deltas));
        }

        /// <summary>
        /// Inserted (blank), corrupt and missing MCUs say nothing about how the part fits, so they are not scored; a
        /// corrupt MCU marked as not bad is.
        /// </summary>
        private bool Scored(McuEntry entry) =>
            entry.Status is not (McuStatus.Blank or McuStatus.Missing) &&
            (entry.Status != McuStatus.Corrupt || document.CountsAsDecoded(entry));

        /// <summary>Median difference over the smoother half of the columns, or null if there are none.</summary>
        private static float? SmoothMedian(List<(float Difference, float Roughness)> diffs)
        {
            if (diffs.Count == 0)
                return null;
            var smooth = diffs.OrderBy(e => e.Roughness).Take((diffs.Count + 1) / 2).Select(e => e.Difference).ToList();
            smooth.Sort();
            return smooth[smooth.Count / 2];
        }

        private float Step(int component) => document._quant[component][0] / 8f;

        /// <summary>Luma counts fully, each chroma component half.</summary>
        private static float Weight(int component) => component == 0 ? 1f : 0.5f;

        private double Cost(Seams[] seams, int[] deltas)
        {
            double sum = 0, weights = 0;
            for (int c = 0; c < seams.Length; c++)
            {
                if (seams[c].Count == 0)
                    continue;
                float shift = deltas[c] * Step(c);
                double mean = 0;
                foreach (var (difference, _) in seams[c].All)
                    mean += Math.Abs(difference - shift);
                sum += Weight(c) * mean / seams[c].Count;
                weights += Weight(c);
            }

            return weights == 0 ? 0 : sum / weights;
        }

        /// <summary>The seam differences of <paramref name="entry"/> placed at <paramref name="position"/> (cached).</summary>
        private Seams[] SeamsAt(McuEntry entry, int position)
        {
            if (_seams.TryGetValue((entry, position), out var cached))
                return cached;
            SeamComputations++;

            var frame = document.Frame;
            var into = new Seams[frame.Components.Count];
            for (int c = 0; c < into.Length; c++)
                into[c] = new Seams();
            _seams[(entry, position)] = into;

            int perRow = frame.McusPerRow;
            int row = position / perRow, col = position % perRow;
            int above = position - perRow;
            if (row == 0 || !reference(above))
                return into;

            var current = Samples(entry);
            var up = Samples(document._mcus[above]);
            int visibleWidth = Math.Min(frame.McuWidth, frame.Width - col * frame.McuWidth);

            for (int c = 0; c < frame.Components.Count; c++)
            {
                var comp = frame.Components[c];
                int planeWidth = comp.H * 8, planeHeight = comp.V * 8;
                int columns = (visibleWidth * comp.H + frame.MaxH - 1) / frame.MaxH;
                int top = document._planeOffset[c], second = top + planeWidth;
                int bottom = top + (planeHeight - 1) * planeWidth, secondLast = bottom - planeWidth;
                for (int x = 0; x < columns; x++)
                {
                    float gradientAbove = up[bottom + x] - up[secondLast + x];
                    float gradientHere = current[second + x] - current[top + x];
                    float edgeAbove = up[bottom + x] + 0.5f * gradientAbove;
                    float edgeHere = current[top + x] - 0.5f * gradientHere;
                    float roughness = Math.Abs(gradientAbove) + Math.Abs(gradientHere) + Math.Abs(gradientAbove - gradientHere);
                    into[c].Above.Add((edgeAbove - edgeHere, roughness));
                }
            }

            return into;
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
