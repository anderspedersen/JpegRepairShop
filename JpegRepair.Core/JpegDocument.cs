namespace JpegRepair.Core;

/// <summary>
/// A section: a run of good MCUs in read order (left to right, row by row), from picture position
/// <paramref name="First"/> to <paramref name="Last"/> inclusive. See <see cref="JpegDocument.FindSection"/>.
/// </summary>
public readonly record struct McuSection(int First, int Last)
{
    public int Count => Last - First + 1;

    public bool Contains(int index) => index >= First && index <= Last;
}

/// <summary>One MCU in the editable sequence.</summary>
public sealed class McuEntry
{
    internal McuEntry(short[] coefficients, McuStatus status, long bitOffset, int bitLength, int sourceIndex)
    {
        Coefficients = coefficients;
        Status = status;
        BitOffset = bitOffset;
        BitLength = bitLength;
        SourceIndex = sourceIndex;
    }

    /// <summary>Quantized coefficients (block order of <see cref="McuLayout"/>, natural order inside each block).</summary>
    public short[] Coefficients { get; }

    public McuStatus Status { get; }

    /// <summary>Bit position in the de-stuffed scan where the MCU was decoded from (0 for blank MCUs).</summary>
    public long BitOffset { get; }

    public int BitLength { get; }

    /// <summary>Index of the MCU in the original decode, or -1 for an MCU inserted by an edit (blank, or filled since).</summary>
    public int SourceIndex { get; }

    /// <summary>A flat grey MCU inserted by an edit and not filled since.</summary>
    public bool IsBlank => Status == McuStatus.Blank;

    /// <summary>Inserted by an edit (not from the original decode).</summary>
    public bool IsInserted => SourceIndex < 0;

    /// <summary>
    /// Cache for the alignment search: the MCU's edge samples computed without the DC coefficients (edits move MCUs and
    /// change DC; filling replaces the entry, so the AC of an entry never changes and this never goes stale).
    /// Layout: see JpegDocument.EdgeLayout.
    /// </summary>
    internal float[]? AcEdges;
}

/// <summary>
/// Editable JPEG: the decoded MCUs as a list in display order, plus undoable repair operations.
/// The first <see cref="FrameHeader.McuCount"/> entries are the picture; entries after that are overflow
/// (pushed out by inserts, or decoded from data left over after the frame) and are not saved.
/// </summary>
public sealed partial class JpegDocument : IMcuSource
{
    private readonly List<McuEntry> _mcus;
    private readonly Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();
    private readonly float[][] _quant;
    private readonly int[] _planeOffset;
    private readonly int[] _blockPlaneOffset;
    private readonly int _samplesPerMcu;
    private int _savedEditId;
    private byte[]? _pixels;
    private BadMcuMap? _detected;   // what detection found, before the user's marks
    private BadMcuMap? _badMcus;    // _detected with the marks applied

    /// <summary>
    /// Pause/stop control for long searches, set by "Fix all" while it runs (null otherwise). The searches call
    /// <see cref="Checkpoint"/> often, only at points where nothing has been changed yet.
    /// </summary>
    public WorkGate? Gate { get; set; }

    /// <summary>Blocks while <see cref="Gate"/> is paused; throws <see cref="OperationCanceledException"/> once it is stopped.</summary>
    private void Checkpoint() => Gate?.Checkpoint();

    // The user's marks (see SetMark), never Auto. Kept per entry, so a mark moves with its MCU on inserts and removes.
    private readonly Dictionary<McuEntry, McuMark> _marks = new(ReferenceEqualityComparer.Instance);

    private JpegDocument(JpegFile file, ScanDecodeResult scan)
    {
        File = file;
        Scan = scan;
        Layout = scan.Layout;

        _mcus = new List<McuEntry>(scan.Mcus.Length + 16);
        for (int i = 0; i < scan.Mcus.Length; i++)
        {
            var info = scan.Mcus[i];
            _mcus.Add(new McuEntry(scan.GetMcuCoefficients(i).ToArray(), info.Status, info.BitOffset, info.BitLength, i));
        }

        var components = file.Frame.Components;
        _quant = new float[components.Count][];
        _planeOffset = new int[components.Count];
        int offset = 0;
        for (int c = 0; c < components.Count; c++)
        {
            var table = file.GetQuantizationTable(components[c].QuantTableId)
                        ?? throw new JpegFormatException($"Missing quantization table {components[c].QuantTableId} for component {components[c].Id}.");
            _quant[c] = table.Values.Select(v => (float)v).ToArray();
            _planeOffset[c] = offset;
            offset += components[c].H * components[c].V * 64;
        }

        _samplesPerMcu = offset;
        _blockPlaneOffset = new int[Layout.BlocksPerMcu];
        for (int b = 0; b < Layout.BlocksPerMcu; b++)
        {
            var block = Layout.Blocks[b];
            int planeWidth = components[block.ComponentIndex].H * 8;
            _blockPlaneOffset[b] = _planeOffset[block.ComponentIndex] + block.BlockY * 8 * planeWidth + block.BlockX * 8;
        }

        Renderer = new JpegRenderer(file, Layout, this);
    }

    /// <summary>Raised after every edit, undo and redo. The argument is the first MCU index whose content changed.</summary>
    public event EventHandler<int>? Changed;

    /// <summary>Raised when the user's marks change without the picture changing (see <see cref="SetMark"/>).</summary>
    public event EventHandler? MarksChanged;

    public JpegFile File { get; }

    /// <summary>The original decode (issues, original bit positions). Not affected by edits.</summary>
    public ScanDecodeResult Scan { get; }

    public McuLayout Layout { get; }

    public FrameHeader Frame => File.Frame;

    public JpegRenderer Renderer { get; }

    public int Width => Frame.Width;

    public int Height => Frame.Height;

    /// <summary>Total entries, including overflow beyond the frame.</summary>
    public int Count => _mcus.Count;

    public int OverflowCount => Math.Max(0, _mcus.Count - Frame.McuCount);

    public McuEntry this[int index] => _mcus[index];

    public bool CanUndo => _undo.Count > 0;

    /// <summary>Number of steps that can be undone (to undo back to an earlier point).</summary>
    public int UndoCount => _undo.Count;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoDescription => _undo.TryPeek(out var edit) ? edit.Description : null;

    public string? RedoDescription => _redo.TryPeek(out var edit) ? edit.Description : null;

    /// <summary>True if the document differs from what was loaded or last saved.</summary>
    public bool IsModified => CurrentEditId != _savedEditId;

    private int CurrentEditId => _undo.TryPeek(out var edit) ? edit.Id : 0;

    public static JpegDocument Decode(byte[] data)
    {
        var file = JpegFile.Parse(data);
        return new JpegDocument(file, ScanDecoder.Decode(file));
    }

    public static JpegDocument Load(string path) => Decode(System.IO.File.ReadAllBytes(path));

    public McuStatus GetStatus(int index) => (uint)index < (uint)_mcus.Count ? _mcus[index].Status : McuStatus.Missing;

    public ReadOnlySpan<short> GetCoefficients(int index) => _mcus[index].Coefficients;

    public short GetDc(int index, int block) => _mcus[index].Coefficients[block * 64];

    public byte[] RenderBgra() => Renderer.RenderBgra();

    /// <summary>
    /// The rendered picture (Width × Height BGRA, stride Width × 4), rendered on first use and kept up to date:
    /// after every edit only the MCU rows from the first changed MCU downwards are re-rendered.
    /// Call <see cref="EnsureRendered"/> on a background thread after loading to keep the first render off the UI thread.
    /// </summary>
    public byte[] Pixels => _pixels ??= Renderer.RenderBgra();

    public void EnsureRendered() => _ = Pixels;

    /// <summary>
    /// MCUs of the picture that don't fit their neighbours (2 or more rugged edges), see <see cref="BadMcuDetector"/>,
    /// with the user's marks applied (<see cref="SetMark"/>; <see cref="BadMcuMap.IsDetected"/> tells what detection
    /// found on its own). Computed from <see cref="Pixels"/> and kept until the next edit, undo or redo.
    /// </summary>
    public BadMcuMap FindBadMcus() =>
        _badMcus ??= (_detected ??= BadMcuDetector.Detect(Pixels, Frame, GetStatus)).WithMarks(StoredMark, IsCorruptAt);

    private McuMark StoredMark(int index) =>
        index < _mcus.Count && _marks.TryGetValue(_mcus[index], out var mark) ? mark : McuMark.Auto;

    private bool IsCorruptAt(int index) => index < _mcus.Count && _mcus[index].Status == McuStatus.Corrupt;

    /// <summary>
    /// True if the entry's data counts as decoded: decoded without errors (Ok), or with a decoding error (Corrupt) but
    /// marked as not bad by the user, because what was decoded still looks right.
    /// </summary>
    private bool CountsAsDecoded(McuEntry entry) =>
        entry.Status == McuStatus.Ok ||
        entry.Status == McuStatus.Corrupt && _marks.TryGetValue(entry, out var mark) && mark == McuMark.NotBad;

    // ---- Overriding bad-MCU detection ------------------------------------------------------------------

    /// <summary>
    /// True if the MCU at picture position <paramref name="index"/> can be marked as <paramref name="mark"/>:
    /// <see cref="McuMark.Bad"/> on any MCU decoded without errors or filled, <see cref="McuMark.NotBad"/> on MCUs detection
    /// flags and on MCUs with a decoding error (whose data often still looks right), <see cref="McuMark.Auto"/> always.
    /// </summary>
    public bool CanMark(int index, McuMark mark)
    {
        if (index < 0 || index >= Math.Min(_mcus.Count, Frame.McuCount))
            return false;
        return mark switch
        {
            McuMark.Bad => _mcus[index].Status is McuStatus.Ok or McuStatus.Filled,
            McuMark.NotBad => _mcus[index].Status == McuStatus.Corrupt || FindBadMcus().IsDetected(index),
            _ => true,
        };
    }

    /// <summary>
    /// Overrides bad-MCU detection for the MCU at picture position <paramref name="index"/>: <see cref="McuMark.Bad"/>
    /// makes it bad, <see cref="McuMark.NotBad"/> makes it good while detection flags it, <see cref="McuMark.Auto"/>
    /// removes the mark. That applies everywhere detection is used: highlighting, sections and the colour estimate.
    /// Returns false (and changes nothing) if <see cref="CanMark"/> says no or the mark is already set.
    /// The mark belongs to the MCU and moves with it when MCUs are inserted or removed before it.
    /// Marks stay until the user changes them, whatever is edited. Not an edit: the picture and the saved file stay the
    /// same, and it is not in the undo history.
    /// </summary>
    public bool SetMark(int index, McuMark mark)
    {
        if (!CanMark(index, mark))
            return false;

        var entry = _mcus[index];
        if (mark == McuMark.Auto)
        {
            if (!_marks.Remove(entry))
                return false;
        }
        else
        {
            if (_marks.TryGetValue(entry, out var current) && current == mark)
                return false;
            _marks[entry] = mark;
        }

        _badMcus = null;
        MarksChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>True if at least one of the user's marks currently changes what counts as bad.</summary>
    public bool HasMarks => FindBadMcus().MarkCount > 0;

    /// <summary>
    /// True if the MCU at picture position <paramref name="index"/> is good: decoded without errors (Ok; or Corrupt and
    /// marked as not bad; not Missing or Blank) and not bad (see <see cref="FindBadMcus"/>: detection with the user's marks applied).
    /// </summary>
    public bool IsGood(int index) => IsGood(index, FindBadMcus());

    private bool IsGood(int index, BadMcuMap bad) =>
        index >= 0 && index < Math.Min(_mcus.Count, Frame.McuCount) && CountsAsDecoded(_mcus[index]) && !bad.IsBad(index);

    /// <summary>
    /// The section around picture position <paramref name="index"/>: from the first to the last good MCU (see
    /// <see cref="IsGood(int)"/>) that can be reached from it in read order without passing a bad one.
    /// Null if the MCU at <paramref name="index"/> is not good itself.
    /// </summary>
    public McuSection? FindSection(int index)
    {
        var bad = FindBadMcus();
        if (!IsGood(index, bad))
            return null;

        int first = index, last = index;
        while (IsGood(first - 1, bad))
            first--;
        while (IsGood(last + 1, bad))
            last++;
        return new McuSection(first, last);
    }

    /// <summary>First MCU row re-rendered by the most recent edit, undo or redo (0 = whole picture).</summary>
    public int LastChangedRow { get; private set; }

    // ---- Edits -------------------------------------------------------------------------------------------

    /// <summary>
    /// Inserts <paramref name="count"/> flat grey MCUs before <paramref name="index"/>, shifting everything after
    /// them right. One undo step.
    /// </summary>
    public void InsertBlank(int index, int count = 1)
    {
        if (index < 0 || index > _mcus.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));

        Apply(InsertBlankEdit(index, count));
    }

    private Edit InsertBlankEdit(int index, int count)
    {
        var blanks = new McuEntry[count];
        for (int i = 0; i < count; i++)
            blanks[i] = new McuEntry(new short[Layout.CoefficientsPerMcu], McuStatus.Blank, 0, 0, -1);
        return new InsertEdit(index, blanks);
    }

    /// <summary>Removes <paramref name="count"/> MCUs from <paramref name="index"/>, shifting everything after them left. One undo step.</summary>
    public void Remove(int index, int count = 1)
    {
        if (index < 0 || index >= _mcus.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        Apply(new RemoveEdit(index, Math.Min(count, _mcus.Count - index)));
    }

    /// <summary>
    /// Adds <paramref name="deltas"/> (one per frame component, in DC quantization steps) to the DC coefficient
    /// of every block of every decoded MCU from <paramref name="fromIndex"/> to the end.
    /// That is how a colour/brightness shift caused by corrupt DC prediction is undone.
    /// </summary>
    /// <param name="mergeWithPrevious">
    /// If the last edit was also a DC adjustment from the same MCU (and nothing was undone or saved since),
    /// add to it instead of creating a new undo step. Used by the nudge buttons, so holding one down
    /// doesn't fill the undo history.
    /// </param>
    public void AdjustDc(int fromIndex, IReadOnlyList<int> deltas, bool mergeWithPrevious = false)
    {
        if (fromIndex < 0 || fromIndex >= _mcus.Count)
            throw new ArgumentOutOfRangeException(nameof(fromIndex));
        if (deltas.Count != Frame.Components.Count)
            throw new ArgumentException("Need one delta per component.", nameof(deltas));
        if (deltas.All(d => d == 0))
            return;

        if (mergeWithPrevious && WouldMergeDc(fromIndex) && _undo.Peek() is DcEdit previous)
        {
            new DcEdit(fromIndex, deltas.ToArray()).Apply(this); // apply just the extra shift
            previous.Add(deltas);
            RenderFrom(fromIndex);
            Changed?.Invoke(this, fromIndex);
            return;
        }

        Apply(new DcEdit(fromIndex, deltas.ToArray()));
    }

    /// <summary>
    /// True if <see cref="AdjustDc"/> at <paramref name="fromIndex"/> with <c>mergeWithPrevious</c> would add to the last
    /// undo step instead of creating a new one.
    /// </summary>
    public bool WouldMergeDc(int fromIndex) =>
        _redo.Count == 0 && _undo.TryPeek(out var top) && top.Id != _savedEditId && top is DcEdit previous && previous.CanMergeWith(fromIndex);

    /// <summary>
    /// How far the DC of <paramref name="block"/> in the MCU at <paramref name="index"/> has been moved from the
    /// value originally decoded (0 for inserted, filled or missing MCUs).
    /// </summary>
    public int GetDcChange(int index, int block)
    {
        if ((uint)index >= (uint)_mcus.Count || _mcus[index].IsInserted || _mcus[index].Status == McuStatus.Filled)
            return 0;
        var entry = _mcus[index];
        return entry.Coefficients[block * 64] - Scan.GetBlock(entry.SourceIndex, block)[0];
    }

    /// <summary>
    /// Estimates the colour shift at <paramref name="index"/> by comparing the bottom sample row of the MCU row above
    /// with the top sample row of one MCU row starting at <paramref name="index"/>, and corrects it from
    /// <paramref name="index"/> onwards. Returns the applied deltas per component (all zero if nothing was changed).
    /// </summary>
    public int[] AutoFixColors(int index)
    {
        var deltas = EstimateDcShift(index);
        if (deltas.Any(d => d != 0))
            Apply(new DcEdit(index, deltas, "Fix colour shift"));
        return deltas;
    }

    /// <summary>
    /// MCU rows' worth of MCUs of the section compared by <see cref="EstimateSectionColourShift"/>, and the default
    /// number of rows' worth of good MCUs before it.
    /// </summary>
    public const int SectionHistogramRows = 2;

    /// <summary>
    /// Fixes the colours of the section around <paramref name="index"/> from colour histograms (see
    /// <see cref="EstimateSectionColourShift"/>): the DC change is applied from the section's first MCU onwards, as one
    /// undo step. Returns the section and the applied deltas (all zero if the colours already match), or null.
    /// </summary>
    /// <param name="referenceRows">Rows' worth of good MCUs before the section to compare with.</param>
    public (McuSection Section, int[] Deltas)? FixSectionColours(int index, int referenceRows = SectionHistogramRows)
    {
        var estimate = EstimateSectionColourShift(index, referenceRows);
        if (estimate is { } found && found.Deltas.Any(d => d != 0))
            Apply(new DcEdit(found.Section.First, found.Deltas, "Fix section colours"));
        return estimate;
    }

    /// <summary>
    /// The colour shift of the section around <paramref name="index"/> (see <see cref="FindSection"/>), found from colour
    /// histograms instead of edges, for places where the edges don't line up (garbage or misaligned rows above).
    /// Per component, the block DC values of the first <see cref="SectionHistogramRows"/> rows' worth of MCUs of the
    /// section and of the last <paramref name="referenceRows"/> rows' worth of good MCUs before it (skipping bad ones,
    /// so a garbage band in between does not count) are put into histograms, and the result is the DC change that makes
    /// the section's histogram overlap the earlier one most (histogram intersection; ties go to the smaller change).
    /// Only nearby MCUs are compared because a picture's colours change from place to place.
    /// Null if there is no section at <paramref name="index"/> or no good MCU before it.
    /// </summary>
    /// <param name="referenceRows">Rows' worth of good MCUs before the section to compare with (at least 1).</param>
    public (McuSection Section, int[] Deltas)? EstimateSectionColourShift(int index, int referenceRows = SectionHistogramRows)
    {
        if (FindSection(index) is not { } section)
            return null;

        var bad = FindBadMcus();
        int window = SectionHistogramRows * Frame.McusPerRow;
        var inside = new List<int>(window);
        for (int i = section.First; i <= section.Last && inside.Count < window; i++)
            inside.Add(i);
        int referenceWindow = Math.Max(1, referenceRows) * Frame.McusPerRow;
        var before = new List<int>(referenceWindow);
        for (int i = section.First - 1; i >= 0 && before.Count < referenceWindow; i--)
        {
            if (IsGood(i, bad))
                before.Add(i);
        }

        if (before.Count == 0)
            return null;

        int components = Frame.Components.Count;
        var deltas = new int[components];
        for (int c = 0; c < components; c++)
        {
            var sectionHistogram = DcHistogram(inside, c, out int sectionCount);
            var beforeHistogram = DcHistogram(before, c, out int beforeCount);
            if (sectionCount == 0 || beforeCount == 0)
                continue;

            // Largest change that can matter: one that moves the samples across the whole 0..255 range.
            int maxShift = (int)MathF.Ceiling(255 * 8 / _quant[c][0]);
            double bestOverlap = -1;
            for (int shift = -maxShift; shift <= maxShift; shift++)
            {
                double overlap = 0;
                for (int v = 0; v < DcHistogramSize; v++)
                {
                    // Section value v, moved by the shift, compared with the earlier MCUs' share at v + shift.
                    if (sectionHistogram[v] == 0 || (uint)(v + shift) >= DcHistogramSize)
                        continue;
                    overlap += Math.Min((double)sectionHistogram[v] / sectionCount, (double)beforeHistogram[v + shift] / beforeCount);
                }

                if (overlap > bestOverlap + 1e-12 || (Math.Abs(overlap - bestOverlap) <= 1e-12 && Math.Abs(shift) < Math.Abs(deltas[c])))
                {
                    bestOverlap = overlap;
                    deltas[c] = shift;
                }
            }
        }

        return (section, deltas);
    }

    private const int DcHistogramOffset = 2048;
    private const int DcHistogramSize = 4096;

    /// <summary>Histogram of the DC values of component <paramref name="component"/>'s blocks in the given MCUs.</summary>
    private int[] DcHistogram(List<int> mcus, int component, out int count)
    {
        var histogram = new int[DcHistogramSize];
        count = 0;
        var blocks = Layout.Blocks;
        foreach (int m in mcus)
        {
            var coefficients = _mcus[m].Coefficients;
            for (int b = 0; b < blocks.Count; b++)
            {
                if (blocks[b].ComponentIndex != component)
                    continue;
                histogram[Math.Clamp(coefficients[b * 64] + DcHistogramOffset, 0, DcHistogramSize - 1)]++;
                count++;
            }
        }

        return histogram;
    }

    /// <summary>
    /// Per component, the DC change (in quantization steps) that best joins the MCUs from <paramref name="index"/>
    /// (one row's worth) to the row above. Both sides are extrapolated linearly to the boundary line (so smooth
    /// gradients don't count as a shift), the smoother half of the columns is kept (edges and texture are unreliable),
    /// and the median difference is taken. Small, highly detailed images can be a few steps off, so the UI also offers
    /// manual nudging.
    ///
    /// MCUs that look like garbage are left out, on both sides (see <see cref="ColourFixSkips"/>): garbage that decoded
    /// without errors would otherwise pull the median.
    /// </summary>
    public int[] EstimateDcShift(int index) => EstimateDcShift(index, ColourFixSkips());

    /// <summary>
    /// MCUs the colour estimate leaves out: every MCU flagged by bad-MCU detection (<see cref="FindBadMcus"/>).
    /// </summary>
    private HashSet<McuEntry> ColourFixSkips()
    {
        var set = new HashSet<McuEntry>(ReferenceEqualityComparer.Instance);
        foreach (int i in FindBadMcus().BadIndices())
        {
            if (i < _mcus.Count)
                set.Add(_mcus[i]);
        }

        return set;
    }

    /// <param name="skip">MCUs not to compare (see <see cref="ColourFixSkips"/>).</param>
    /// <param name="count">MCUs to compare from <paramref name="index"/> on (0: one row's worth).</param>
    private int[] EstimateDcShift(int index, HashSet<McuEntry> skip, int count = 0)
    {
        var frame = Frame;
        int components = frame.Components.Count;
        var result = new int[components];
        int perRow = frame.McusPerRow;
        if (index < perRow || index >= Math.Min(_mcus.Count, frame.McuCount))
            return result;

        var differences = new List<(float Difference, float Roughness)>[components];
        for (int c = 0; c < components; c++)
            differences[c] = [];

        var above = new float[_samplesPerMcu];
        var current = new float[_samplesPerMcu];
        int end = Math.Min(index + (count > 0 ? count : perRow), Math.Min(_mcus.Count, frame.McuCount));

        for (int k = index; k < end; k++)
        {
            if (!CountsAsDecoded(_mcus[k - perRow]) || !CountsAsDecoded(_mcus[k])
                || skip.Contains(_mcus[k - perRow]) || skip.Contains(_mcus[k]))
                continue;

            ComponentSamples(k - perRow, above);
            ComponentSamples(k, current);

            // Only the part of the MCU that is inside the image.
            int col = k % perRow;
            int visibleWidth = Math.Min(frame.McuWidth, frame.Width - col * frame.McuWidth);

            for (int c = 0; c < components; c++)
            {
                var comp = frame.Components[c];
                int planeWidth = comp.H * 8, planeHeight = comp.V * 8;
                int columns = (visibleWidth * comp.H + frame.MaxH - 1) / frame.MaxH;
                int last = _planeOffset[c] + (planeHeight - 1) * planeWidth;   // bottom row of the MCU above
                int secondLast = last - planeWidth;
                int first = _planeOffset[c];                                   // top row of the current MCU
                int second = first + planeWidth;
                for (int x = 0; x < columns; x++)
                {
                    float gradientAbove = above[last + x] - above[secondLast + x];
                    float gradientBelow = current[second + x] - current[first + x];
                    float edgeAbove = above[last + x] + 0.5f * gradientAbove;
                    float edgeBelow = current[first + x] - 0.5f * gradientBelow;
                    float roughness = Math.Abs(gradientAbove) + Math.Abs(gradientBelow) + Math.Abs(gradientAbove - gradientBelow);
                    differences[c].Add((edgeAbove - edgeBelow, roughness));
                }
            }
        }

        for (int c = 0; c < components; c++)
        {
            var all = differences[c];
            if (all.Count == 0)
                continue;
            all.Sort((a, b) => a.Roughness.CompareTo(b.Roughness));
            var smooth = all.Take((all.Count + 1) / 2).Select(e => e.Difference).ToList();
            smooth.Sort();
            float median = smooth[smooth.Count / 2];
            // A DC step of 1 moves every sample of the block by q0 / 8.
            float step = _quant[c][0] / 8f;
            result[c] = (int)MathF.Round(median / step);
        }

        return result;
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var edit))
            return;
        edit.Revert(this);
        _redo.Push(edit);
        RenderFrom(edit.FirstMcu, edit.LastMcu);
        Changed?.Invoke(this, edit.FirstMcu);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var edit))
            return;
        edit.Apply(this);
        _undo.Push(edit);
        RenderFrom(edit.FirstMcu, edit.LastMcu);
        Changed?.Invoke(this, edit.FirstMcu);
    }

    /// <summary>Encodes the picture (the first McuCount entries; missing ones become flat grey) as a complete JPEG file.</summary>
    public byte[] Save()
    {
        int count = Frame.McuCount;
        int perMcu = Layout.CoefficientsPerMcu;
        var coefficients = new short[count * perMcu];
        for (int i = 0; i < Math.Min(count, _mcus.Count); i++)
            _mcus[i].Coefficients.CopyTo(coefficients, i * perMcu);

        var bytes = JpegWriter.Write(File, ScanEncoder.Encode(File, Layout, coefficients));
        _savedEditId = CurrentEditId;
        return bytes;
    }

    /// <summary>
    /// Redraws the MCU rows from the one holding <paramref name="firstMcu"/> to the one holding
    /// <paramref name="lastMcu"/> (to the bottom by default) and forgets the bad-MCU detection.
    /// </summary>
    private void RenderFrom(int firstMcu, int lastMcu = int.MaxValue)
    {
        int rows = Frame.McuRows;
        LastChangedRow = Math.Clamp(firstMcu / Frame.McusPerRow, 0, rows);
        int lastRow = lastMcu == int.MaxValue ? rows - 1 : Math.Min(lastMcu / Frame.McusPerRow, rows - 1);
        _detected = null;
        _badMcus = null;
        if (_pixels != null && lastRow >= LastChangedRow)
            Renderer.RenderMcuRows(LastChangedRow, lastRow - LastChangedRow + 1, _pixels, Width * 4);
    }

    /// <summary>Unclamped samples of every component plane of one MCU (layout: component planes back to back, H*8 × V*8 each).</summary>
    private void ComponentSamples(int index, float[] dest) => EntrySamples(_mcus[index], dest);

    /// <summary>Same as <see cref="ComponentSamples"/>, for an entry wherever it is.</summary>
    private void EntrySamples(McuEntry entry, float[] dest)
    {
        var coefficients = entry.Coefficients;
        for (int b = 0; b < Layout.BlocksPerMcu; b++)
        {
            int component = Layout.Blocks[b].ComponentIndex;
            Idct.TransformToFloat(coefficients.AsSpan(b * 64, 64), _quant[component],
                dest.AsSpan(_blockPlaneOffset[b]), Frame.Components[component].H * 8);
        }
    }

    private void Apply(Edit edit)
    {
        edit.Apply(this);
        Record(edit);
    }

    /// <summary>Adds an edit that has already been applied to the undo history and updates the picture.</summary>
    private void Record(Edit edit)
    {
        _undo.Push(edit);
        _redo.Clear();
        RenderFrom(edit.FirstMcu, edit.LastMcu);
        Changed?.Invoke(this, edit.FirstMcu);
    }

    // ---- Undoable edit records -----------------------------------------------------------------------------

    private abstract class Edit(int firstMcu)
    {
        private static int _nextId;

        public int Id { get; } = Interlocked.Increment(ref _nextId);

        public int FirstMcu { get; } = firstMcu;

        /// <summary>Last MCU whose content can change (everything after the first by default).</summary>
        public virtual int LastMcu => int.MaxValue;

        public abstract string Description { get; }

        public abstract void Apply(JpegDocument document);

        public abstract void Revert(JpegDocument document);
    }

    /// <summary>Several edits that are undone and redone together.</summary>
    private sealed class CompositeEdit(string description, Edit[] parts) : Edit(parts.Min(p => p.FirstMcu))
    {
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

    private sealed class InsertEdit(int index, McuEntry[] entries) : Edit(index)
    {
        public override string Description => entries.Length == 1
            ? $"Insert blank MCU at {index}"
            : $"Insert {entries.Length} blank MCUs at {index}";

        public override void Apply(JpegDocument document) => document._mcus.InsertRange(index, entries);

        public override void Revert(JpegDocument document) => document._mcus.RemoveRange(index, entries.Length);
    }

    private sealed class RemoveEdit(int index, int count) : Edit(index)
    {
        private McuEntry[]? _removed;

        public override string Description => count == 1 ? $"Remove MCU {index}" : $"Remove {count} MCUs from {index}";

        public override void Apply(JpegDocument document)
        {
            _removed = document._mcus.GetRange(index, count).ToArray();
            document._mcus.RemoveRange(index, count);
        }

        public override void Revert(JpegDocument document) => document._mcus.InsertRange(index, _removed!);
    }

    private sealed class DcEdit(int fromIndex, int[] deltas, string name = "Adjust DC") : Edit(fromIndex)
    {
        public bool CanMergeWith(int index) => index == fromIndex && name == "Adjust DC";

        public void Add(IReadOnlyList<int> more)
        {
            for (int c = 0; c < deltas.Length; c++)
                deltas[c] += more[c];
        }

        public override string Description => $"{name} from MCU {fromIndex} ({string.Join(", ", deltas.Select(d => d.ToString("+0;-0;0")))})";

        public override void Apply(JpegDocument document) => Shift(document, +1);

        public override void Revert(JpegDocument document) => Shift(document, -1);

        private void Shift(JpegDocument document, int sign)
        {
            var blocks = document.Layout.Blocks;
            for (int i = fromIndex; i < document._mcus.Count; i++)
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
}
