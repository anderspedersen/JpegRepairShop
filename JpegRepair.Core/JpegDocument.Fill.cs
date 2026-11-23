namespace JpegRepair.Core;

/// <summary>
/// New contents for MCUs, computed by <see cref="JpegDocument.PrepareFill"/> (which can run on a worker thread) and
/// applied by <see cref="JpegDocument.ApplyFill"/>.
/// </summary>
public sealed class FillPlan
{
    internal FillPlan(int editId, int[] indices, short[][] coefficients)
    {
        EditId = editId;
        IndexArray = indices;
        Coefficients = coefficients;
    }

    /// <summary>Picture positions that get new contents, in increasing order.</summary>
    public IReadOnlyList<int> Indices => IndexArray;

    internal int[] IndexArray { get; }

    internal short[][] Coefficients { get; }

    /// <summary>The document's last edit when the plan was made: the plan only fits that state.</summary>
    internal int EditId { get; }
}

public sealed partial class JpegDocument
{
    /// <summary>Patches compared when filling are (2 × 4 + 1)² = 9 × 9 pixels.</summary>
    private const int FillPatchRadius = 4;

    /// <summary>Of each matched patch, the 3 × 3 pixels around its centre are filled.</summary>
    private const int FillCopyRadius = 1;

    /// <summary>Matching patches are searched for within this many pixels of the patch being filled.</summary>
    private const int FillSearchRadius = 48;

    /// <summary>
    /// The MCUs "Fill" replaces by default: every picture position that is bad (see <see cref="FindBadMcus"/>,
    /// including the user's marks), Corrupt (unless marked as not bad) or Blank.
    /// </summary>
    public IReadOnlyList<int> FindFillTargets()
    {
        var bad = FindBadMcus();
        int count = Math.Min(_mcus.Count, Frame.McuCount);
        var targets = new List<int>();
        for (int i = 0; i < count; i++)
        {
            var status = _mcus[i].Status;
            if (status == McuStatus.Blank || status == McuStatus.Corrupt && !CountsAsDecoded(_mcus[i]) || bad.IsBad(i))
                targets.Add(i);
        }

        return targets;
    }

    /// <summary>Fills the given MCUs (see <see cref="PrepareFill"/>) as one undo step. Returns the number filled.</summary>
    public int FillMcus(IEnumerable<int> targets) => ApplyFill(PrepareFill(targets));

    /// <summary>
    /// Works out new contents for the MCUs at the given picture positions from the picture around them, with
    /// exemplar-based inpainting (see <see cref="Inpainter"/>) on the decoded samples: each group of touching MCUs is
    /// filled in one go, using intact MCUs (Ok and not bad) as the source of patches and already filled MCUs as
    /// context. The result is turned back into quantized coefficients with the file's own tables, adjusted where needed
    /// so that the file's Huffman tables can still encode them.
    /// Changes nothing; apply the result with <see cref="ApplyFill"/>. Can run on a worker thread as long as the
    /// document is not edited meanwhile. Missing positions (never decoded) are left out.
    /// </summary>
    public FillPlan PrepareFill(IEnumerable<int> targets, CancellationToken cancellation = default)
    {
        int editId = CurrentEditId;
        int count = Math.Min(_mcus.Count, Frame.McuCount);
        var targetSet = new HashSet<int>(targets.Where(i => i >= 0 && i < count && _mcus[i].Status != McuStatus.Missing));
        if (targetSet.Count == 0)
            return new FillPlan(editId, [], []);

        var bad = FindBadMcus();
        var filled = new Dictionary<int, float[][]>();
        foreach (var group in TouchingGroups(targetSet))
        {
            cancellation.ThrowIfCancellationRequested();
            FillGroup(group, targetSet, bad, filled, cancellation);
        }

        var indices = targetSet.OrderBy(i => i).ToArray();
        var coefficients = new short[indices.Length][];
        for (int k = 0; k < indices.Length; k++)
            coefficients[k] = ToCoefficients(filled[indices[k]]);
        MakeEncodable(indices, coefficients);
        return new FillPlan(editId, indices, coefficients);
    }

    /// <summary>
    /// Replaces the MCUs of a plan from <see cref="PrepareFill"/> with their new contents (status
    /// <see cref="McuStatus.Filled"/>), as one undo step. Returns the number of MCUs filled.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document was edited after the plan was made.</exception>
    public int ApplyFill(FillPlan plan)
    {
        if (plan.EditId != CurrentEditId)
            throw new InvalidOperationException("The picture was changed while the fill was being worked out.");
        if (plan.IndexArray.Length == 0)
            return 0;

        var indices = plan.IndexArray;
        var entries = new McuEntry[indices.Length];
        for (int k = 0; k < indices.Length; k++)
        {
            var old = _mcus[indices[k]];
            entries[k] = new McuEntry(plan.Coefficients[k], McuStatus.Filled, old.BitOffset, old.BitLength, old.SourceIndex);
        }

        string description = indices.Length == 1 ? $"Fill MCU {indices[0]}" : $"Fill {indices.Length} MCUs";
        Apply(new ReplaceEdit(indices, entries, description));
        return indices.Length;
    }

    /// <summary>Groups of target MCUs that touch (also diagonally), each in increasing order.</summary>
    private List<List<int>> TouchingGroups(HashSet<int> targets)
    {
        int perRow = Frame.McusPerRow, rows = Frame.McuRows;
        var seen = new HashSet<int>();
        var groups = new List<List<int>>();
        foreach (int start in targets.OrderBy(i => i))
        {
            if (!seen.Add(start))
                continue;
            var group = new List<int>();
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.TryPop(out int i))
            {
                group.Add(i);
                int row = i / perRow, col = i % perRow;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int r = row + dy, c = col + dx;
                        if (r < 0 || c < 0 || r >= rows || c >= perRow)
                            continue;
                        int n = r * perRow + c;
                        if (targets.Contains(n) && seen.Add(n))
                            stack.Push(n);
                    }
                }
            }

            group.Sort();
            groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    /// Fills one group of touching MCUs: cuts out the picture around them (far enough for the patch search), marks
    /// each pixel by what its MCU may be used for, inpaints, and stores the group's new pixels (full resolution, one
    /// array per component, McuWidth × McuHeight) in <paramref name="filled"/>.
    /// </summary>
    private void FillGroup(List<int> group, HashSet<int> allTargets, BadMcuMap bad, Dictionary<int, float[][]> filled,
        CancellationToken cancellation)
    {
        var frame = Frame;
        int perRow = frame.McusPerRow, mw = frame.McuWidth, mh = frame.McuHeight;
        int components = frame.Components.Count;

        int minCol = group.Min(i => i % perRow), maxCol = group.Max(i => i % perRow);
        int minRow = group[0] / perRow, maxRow = group[^1] / perRow;
        int margin = FillSearchRadius + FillPatchRadius + 1;
        int x0 = Math.Max(0, minCol * mw - margin), y0 = Math.Max(0, minRow * mh - margin);
        int x1 = Math.Min(Width, (maxCol + 1) * mw + margin), y1 = Math.Min(Height, (maxRow + 1) * mh + margin);
        int w = x1 - x0, h = y1 - y0;

        var channels = new float[components][];
        for (int c = 0; c < components; c++)
            channels[c] = new float[w * h];
        var state = new byte[w * h];
        var samples = new float[_samplesPerMcu];
        var inGroup = new HashSet<int>(group);

        for (int row = y0 / mh; row <= (y1 - 1) / mh; row++)
        {
            for (int col = x0 / mw; col <= (x1 - 1) / mw; col++)
            {
                int index = row * perRow + col;
                byte kind = FillRole(index, inGroup, allTargets, bad, filled);
                int px0 = Math.Max(col * mw, x0), px1 = Math.Min((col + 1) * mw, x1);
                int py0 = Math.Max(row * mh, y0), py1 = Math.Min((row + 1) * mh, y1);

                bool known = kind is Inpainter.Source or Inpainter.Context;
                var pixels = known ? filled.GetValueOrDefault(index) : null;
                bool fromFill = pixels != null;
                if (known && !fromFill)
                    ComponentSamples(index, samples);

                for (int y = py0; y < py1; y++)
                {
                    int my = y - row * mh;
                    for (int x = px0; x < px1; x++)
                    {
                        int mx = x - col * mw;
                        int p = (y - y0) * w + (x - x0);
                        state[p] = kind;
                        if (!known)
                            continue;
                        for (int c = 0; c < components; c++)
                        {
                            float value = fromFill
                                ? pixels![c][my * mw + mx]
                                : Math.Clamp(samples[SampleOffset(c, mx, my)], 0f, 255f);
                            channels[c][p] = value;
                        }
                    }
                }
            }
        }

        new Inpainter(channels, state, w, h, FillPatchRadius, FillCopyRadius, FillSearchRadius).Run(cancellation);

        foreach (int index in group)
        {
            int col = index % perRow, row = index / perRow;
            var pixels = new float[components][];
            for (int c = 0; c < components; c++)
                pixels[c] = new float[mw * mh];

            for (int my = 0; my < mh; my++)
            {
                // Parts of edge MCUs outside the picture repeat the last row or column inside it.
                int y = Math.Min(row * mh + my, Height - 1) - y0;
                for (int mx = 0; mx < mw; mx++)
                {
                    int x = Math.Min(col * mw + mx, Width - 1) - x0;
                    for (int c = 0; c < components; c++)
                        pixels[c][my * mw + mx] = channels[c][y * w + x];
                }
            }

            filled[index] = pixels;
        }
    }

    /// <summary>What the pixels of the MCU at <paramref name="index"/> are when filling <paramref name="group"/>.</summary>
    private byte FillRole(int index, HashSet<int> group, HashSet<int> allTargets, BadMcuMap bad, Dictionary<int, float[][]> filled)
    {
        if (group.Contains(index))
            return Inpainter.Target;
        if (filled.ContainsKey(index))
            return Inpainter.Context;     // filled earlier in this plan
        if (index >= _mcus.Count || allTargets.Contains(index) || bad.IsBad(index))
            return Inpainter.Unavailable; // missing, still to be filled, or garbage
        if (CountsAsDecoded(_mcus[index]))
            return Inpainter.Source;      // decoded (or corrupt and marked as not bad)
        return _mcus[index].Status switch
        {
            McuStatus.Filled => Inpainter.Context,
            _ => Inpainter.Unavailable,
        };
    }

    /// <summary>
    /// Offset in the <see cref="ComponentSamples"/> buffer of component <paramref name="component"/>'s sample shown at
    /// pixel (<paramref name="x"/>, <paramref name="y"/>) of the MCU (nearest neighbour, as the renderer does).
    /// </summary>
    private int SampleOffset(int component, int x, int y)
    {
        var comp = Frame.Components[component];
        return _planeOffset[component] + (y * comp.V / Frame.MaxV) * (comp.H * 8) + x * comp.H / Frame.MaxH;
    }

    /// <summary>
    /// Quantized coefficients for an MCU from its full-resolution pixels: each component is brought to its own
    /// resolution (mean of the pixels each sample covers), transformed and quantized with the file's table.
    /// </summary>
    private short[] ToCoefficients(float[][] pixels)
    {
        var frame = Frame;
        int mw = frame.McuWidth, mh = frame.McuHeight;
        var coefficients = new short[Layout.CoefficientsPerMcu];

        // Per component: the plane of samples (H·8 × V·8) as the mean of the pixels mapped to each sample.
        var planes = new float[frame.Components.Count][];
        for (int c = 0; c < planes.Length; c++)
        {
            var comp = frame.Components[c];
            int planeWidth = comp.H * 8;
            var sum = new float[planeWidth * comp.V * 8];
            var count = new int[sum.Length];
            for (int y = 0; y < mh; y++)
            {
                int planeRow = (y * comp.V / frame.MaxV) * planeWidth;
                for (int x = 0; x < mw; x++)
                {
                    int s = planeRow + x * comp.H / frame.MaxH;
                    sum[s] += pixels[c][y * mw + x];
                    count[s]++;
                }
            }

            for (int s = 0; s < sum.Length; s++)
                sum[s] = count[s] > 0 ? sum[s] / count[s] : 128f;
            planes[c] = sum;
        }

        Span<float> block = stackalloc float[64];
        Span<float> transformed = stackalloc float[64];
        var blocks = Layout.Blocks;
        for (int b = 0; b < blocks.Count; b++)
        {
            var info = blocks[b];
            int c = info.ComponentIndex;
            int planeWidth = frame.Components[c].H * 8;
            var plane = planes[c];
            for (int y = 0; y < 8; y++)
            {
                int planeRow = (info.BlockY * 8 + y) * planeWidth + info.BlockX * 8;
                for (int x = 0; x < 8; x++)
                    block[y * 8 + x] = plane[planeRow + x] - 128f;
            }

            Fdct.Forward(block, transformed);
            var quant = _quant[c];
            // Baseline range; the DC is moved into its own range by MakeEncodable.
            for (int k = 0; k < 64; k++)
                coefficients[b * 64 + k] = (short)Math.Clamp((int)MathF.Round(transformed[k] / quant[k]), -1023, 1023);
        }

        return coefficients;
    }

    /// <summary>
    /// Adjusts filled coefficients so the file's Huffman tables can encode them: every AC value must have a
    /// (run, size) code, and every DC difference (to the previous block of the same component, and from the next block
    /// that is not being filled) a size code. Optimised tables only contain the codes the original picture used.
    /// Values are made smaller (AC) or moved to the nearest encodable value (DC) where needed.
    /// </summary>
    private void MakeEncodable(int[] indices, short[][] coefficients)
    {
        var (dcTables, acTables) = Layout.ResolveHuffmanTables(File);
        var planned = new Dictionary<int, short[]>(indices.Length);
        for (int k = 0; k < indices.Length; k++)
            planned[indices[k]] = coefficients[k];

        var blocks = Layout.Blocks;
        for (int k = 0; k < indices.Length; k++)
        {
            int m = indices[k];
            var mcu = coefficients[k];
            for (int b = 0; b < blocks.Count; b++)
            {
                FitAcToTable(mcu.AsSpan(b * 64, 64), acTables[b]);

                int component = blocks[b].ComponentIndex;
                int previous = DcBefore(m, b, component, planned);
                int? next = DcAfter(m, b, component, planned);
                mcu[b * 64] = (short)FitDcToTable(mcu[b * 64], previous, next, dcTables[b]);
            }
        }
    }

    /// <summary>The DC the encoder predicts block <paramref name="block"/> of MCU <paramref name="m"/> from.</summary>
    private int DcBefore(int m, int block, int component, Dictionary<int, short[]> planned)
    {
        var blocks = Layout.Blocks;
        for (int b = block - 1; b >= 0; b--)
        {
            if (blocks[b].ComponentIndex == component)
                return EncodedDc(m, b, planned);
        }

        int interval = File.RestartInterval;
        if (m == 0 || (interval > 0 && m % interval == 0))
            return 0;
        for (int b = blocks.Count - 1; b >= 0; b--)
        {
            if (blocks[b].ComponentIndex == component)
                return EncodedDc(m - 1, b, planned);
        }

        return 0;
    }

    /// <summary>
    /// The DC of the block coded right after block <paramref name="block"/> of MCU <paramref name="m"/> in the same
    /// component, if its difference depends on this block and it is not being filled itself (then it is adjusted later).
    /// </summary>
    private int? DcAfter(int m, int block, int component, Dictionary<int, short[]> planned)
    {
        var blocks = Layout.Blocks;
        for (int b = block + 1; b < blocks.Count; b++)
        {
            if (blocks[b].ComponentIndex == component)
                return null; // in the same MCU, which is being filled: adjusted after this one
        }

        int interval = File.RestartInterval;
        int nextMcu = m + 1;
        if (nextMcu >= Frame.McuCount || (interval > 0 && nextMcu % interval == 0) || planned.ContainsKey(nextMcu))
            return null;
        for (int b = 0; b < blocks.Count; b++)
        {
            if (blocks[b].ComponentIndex == component)
                return EncodedDc(nextMcu, b, planned);
        }

        return null;
    }

    /// <summary>DC of a block as the encoder writes it (clamped to the baseline range; 0 for positions never decoded).</summary>
    private int EncodedDc(int m, int block, Dictionary<int, short[]> planned)
    {
        int value = planned.TryGetValue(m, out var mcu) ? mcu[block * 64]
            : m < _mcus.Count ? _mcus[m].Coefficients[block * 64] : 0;
        return Math.Clamp(value, -1024, 1023);
    }

    /// <summary>The DC value closest to <paramref name="wanted"/> whose differences the table can encode.</summary>
    internal static int FitDcToTable(int wanted, int previous, int? next, HuffmanTable table)
    {
        bool Encodable(int value) =>
            table.TryGetCode(BitSize(value - previous), out _, out _)
            && (next is not { } n || table.TryGetCode(BitSize(n - value), out _, out _));

        wanted = Math.Clamp(wanted, -1024, 1023);
        for (int delta = 0; delta <= 2047; delta++)
        {
            if (wanted - delta >= -1024 && Encodable(wanted - delta))
                return wanted - delta;
            if (wanted + delta <= 1023 && Encodable(wanted + delta))
                return wanted + delta;
        }

        return wanted;
    }

    /// <summary>
    /// Makes the AC coefficients of one block (natural order) encodable with <paramref name="table"/>: in zigzag order,
    /// a value whose (run, size) symbol is missing is made smaller until it has one, or set to 0; a run of more than 15
    /// zeros without a ZRL code drops the value after it.
    /// </summary>
    internal static void FitAcToTable(Span<short> block, HuffmanTable table)
    {
        var zigzag = ZigZag.ToNatural;
        bool hasZrl = table.TryGetCode(0xF0, out _, out _);
        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int at = zigzag[k];
            int value = block[at];
            if (value == 0)
            {
                run++;
                continue;
            }

            if (run > 15 && !hasZrl)
            {
                block[at] = 0;
                run++;
                continue;
            }

            int r = run & 15;
            int sign = value < 0 ? -1 : 1;
            int size = Math.Min(BitSize(value), 10);
            while (size > 0 && !table.TryGetCode((r << 4) | size, out _, out _))
                size--;

            if (size == 0)
            {
                block[at] = 0;
                run++;
                continue;
            }

            int largest = (1 << size) - 1;
            if (Math.Abs(value) > largest)
                block[at] = (short)(sign * largest);
            run = 0;
        }
    }

    private static int BitSize(int value)
    {
        uint magnitude = (uint)Math.Abs(value);
        return magnitude == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(magnitude);
    }

    /// <summary>Replaces MCUs at given positions with new entries (undo puts the old ones back).</summary>
    private sealed class ReplaceEdit(int[] indices, McuEntry[] entries, string description) : Edit(indices[0])
    {
        private McuEntry[]? _previous;

        public override string Description => description;

        public override void Apply(JpegDocument document)
        {
            _previous ??= indices.Select(i => document._mcus[i]).ToArray();
            for (int k = 0; k < indices.Length; k++)
                document._mcus[indices[k]] = entries[k];
        }

        public override void Revert(JpegDocument document)
        {
            for (int k = 0; k < indices.Length; k++)
                document._mcus[indices[k]] = _previous![k];
        }
    }
}
