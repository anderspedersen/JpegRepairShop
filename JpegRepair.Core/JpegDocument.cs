namespace JpegRepair.Core;

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

    /// <summary>Index of the MCU in the original decode, or -1 for a blank MCU inserted by an edit.</summary>
    public int SourceIndex { get; }

    public bool IsBlank => SourceIndex < 0;
}

/// <summary>
/// Editable JPEG: the decoded MCUs as a list in display order, plus undoable repair operations.
/// The first <see cref="FrameHeader.McuCount"/> entries are the picture; entries after that are overflow
/// (pushed out by inserts, or decoded from data left over after the frame) and are not saved.
/// </summary>
public sealed class JpegDocument : IMcuSource
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

    /// <summary>First MCU row re-rendered by the most recent edit, undo or redo (0 = whole picture).</summary>
    public int LastChangedRow { get; private set; }

    // ---- Edits -------------------------------------------------------------------------------------------

    /// <summary>Inserts a flat grey MCU before <paramref name="index"/>, shifting everything after it right by one.</summary>
    public void InsertBlank(int index)
    {
        if (index < 0 || index > _mcus.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        Apply(new InsertEdit(index, new McuEntry(new short[Layout.CoefficientsPerMcu], McuStatus.Blank, 0, 0, -1)));
    }

    /// <summary>Removes the MCU at <paramref name="index"/>, shifting everything after it left by one.</summary>
    public void Remove(int index)
    {
        if (index < 0 || index >= _mcus.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        Apply(new RemoveEdit(index));
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

        if (mergeWithPrevious && _redo.Count == 0 && _undo.TryPeek(out var top) && top.Id != _savedEditId
            && top is DcEdit previous && previous.CanMergeWith(fromIndex))
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
    /// How far the DC of <paramref name="block"/> in the MCU at <paramref name="index"/> has been moved from the
    /// value originally decoded (0 for blank or missing MCUs).
    /// </summary>
    public int GetDcChange(int index, int block)
    {
        if ((uint)index >= (uint)_mcus.Count || _mcus[index].IsBlank)
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
    /// Per component, the DC change (in quantization steps) that best joins the MCUs from <paramref name="index"/>
    /// (one row's worth) to the row above. Both sides are extrapolated linearly to the boundary line (so smooth
    /// gradients don't count as a shift), the smoother half of the columns is kept (edges and texture are unreliable),
    /// and the median difference is taken. Checked on the sample photos: exact or ±1 step for ~97 % of rows of a
    /// normal photo; small, highly detailed images can be a few steps off, so the UI also offers manual nudging.
    /// </summary>
    public int[] EstimateDcShift(int index)
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
        int end = Math.Min(index + perRow, Math.Min(_mcus.Count, frame.McuCount));

        for (int k = index; k < end; k++)
        {
            if (_mcus[k - perRow].Status != McuStatus.Ok || _mcus[k].Status != McuStatus.Ok)
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
        RenderFrom(edit.FirstMcu);
        Changed?.Invoke(this, edit.FirstMcu);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var edit))
            return;
        edit.Apply(this);
        _undo.Push(edit);
        RenderFrom(edit.FirstMcu);
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

    private void RenderFrom(int firstMcu)
    {
        LastChangedRow = Math.Clamp(firstMcu / Frame.McusPerRow, 0, Frame.McuRows);
        if (_pixels != null)
            Renderer.RenderMcuRows(LastChangedRow, Frame.McuRows - LastChangedRow, _pixels, Width * 4);
    }

    /// <summary>Unclamped samples of every component plane of one MCU (layout: component planes back to back, H*8 × V*8 each).</summary>
    private void ComponentSamples(int index, float[] dest)
    {
        var coefficients = _mcus[index].Coefficients;
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
        _undo.Push(edit);
        _redo.Clear();
        RenderFrom(edit.FirstMcu);
        Changed?.Invoke(this, edit.FirstMcu);
    }

    // ---- Undoable edit records -----------------------------------------------------------------------------

    private abstract class Edit(int firstMcu)
    {
        private static int _nextId;

        public int Id { get; } = Interlocked.Increment(ref _nextId);

        public int FirstMcu { get; } = firstMcu;

        public abstract string Description { get; }

        public abstract void Apply(JpegDocument document);

        public abstract void Revert(JpegDocument document);
    }

    private sealed class InsertEdit(int index, McuEntry entry) : Edit(index)
    {
        public override string Description => $"Insert blank MCU at {index}";

        public override void Apply(JpegDocument document) => document._mcus.Insert(index, entry);

        public override void Revert(JpegDocument document) => document._mcus.RemoveAt(index);
    }

    private sealed class RemoveEdit(int index) : Edit(index)
    {
        private McuEntry? _removed;

        public override string Description => $"Remove MCU {index}";

        public override void Apply(JpegDocument document)
        {
            _removed = document._mcus[index];
            document._mcus.RemoveAt(index);
        }

        public override void Revert(JpegDocument document) => document._mcus.Insert(index, _removed!);
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
