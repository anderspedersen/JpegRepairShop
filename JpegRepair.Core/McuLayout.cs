namespace JpegRepair.Core;

/// <param name="ScanComponent">Index into <see cref="ScanHeader.Components"/>.</param>
/// <param name="ComponentIndex">Index into <see cref="FrameHeader.Components"/>.</param>
/// <param name="BlockX">Horizontal block position inside the component's part of the MCU.</param>
/// <param name="BlockY">Vertical block position inside the component's part of the MCU.</param>
public readonly record struct BlockInfo(int ScanComponent, int ComponentIndex, int BlockX, int BlockY);

/// <summary>The order of 8x8 blocks inside one MCU, as they appear in the bit stream.</summary>
public sealed class McuLayout
{
    public McuLayout(FrameHeader frame, ScanHeader scan)
    {
        if (scan.Components.Count != frame.Components.Count)
            throw new JpegNotSupportedException("JPEGs with a separate scan per component are not supported yet.");

        Frame = frame;
        Scan = scan;

        var blocks = new List<BlockInfo>();
        for (int s = 0; s < scan.Components.Count; s++)
        {
            var c = scan.Components[s].Component;
            for (int by = 0; by < c.V; by++)
                for (int bx = 0; bx < c.H; bx++)
                    blocks.Add(new BlockInfo(s, c.Index, bx, by));
        }

        BlockArray = blocks.ToArray();
    }

    public FrameHeader Frame { get; }

    public ScanHeader Scan { get; }

    public IReadOnlyList<BlockInfo> Blocks => BlockArray;

    /// <summary>Same as <see cref="Blocks"/>, as an array for hot loops.</summary>
    internal BlockInfo[] BlockArray { get; }

    public int BlocksPerMcu => BlockArray.Length;

    public int CoefficientsPerMcu => BlockArray.Length * 64;

    /// <summary>Resolves the DC and AC Huffman table for each block of the MCU.</summary>
    internal (HuffmanTable[] Dc, HuffmanTable[] Ac) ResolveHuffmanTables(JpegFile file)
    {
        var dc = new HuffmanTable[Blocks.Count];
        var ac = new HuffmanTable[Blocks.Count];
        for (int b = 0; b < Blocks.Count; b++)
        {
            var sc = Scan.Components[Blocks[b].ScanComponent];
            dc[b] = file.GetDcTable(sc.DcTableId)
                    ?? throw new JpegFormatException($"Missing DC Huffman table {sc.DcTableId} for component {sc.Component.Id}.");
            ac[b] = file.GetAcTable(sc.AcTableId)
                    ?? throw new JpegFormatException($"Missing AC Huffman table {sc.AcTableId} for component {sc.Component.Id}.");
        }

        return (dc, ac);
    }
}
