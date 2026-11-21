namespace JpegRepair.Core;

/// <param name="Marker">Marker byte (the byte after 0xFF).</param>
/// <param name="Offset">Offset of the 0xFF byte in the file.</param>
/// <param name="Length">Value of the segment's length field (includes the two length bytes; 0 for SOI).</param>
public sealed record JpegSegment(byte Marker, int Offset, int Length);

/// <summary>
/// A parsed JPEG file: headers and tables up to and including the first scan header,
/// plus the extracted entropy-coded data. The original bytes are kept so the file can be
/// written back with every header segment (EXIF, ICC, comments...) untouched.
/// </summary>
public sealed class JpegFile
{
    private readonly List<JpegSegment> _segments = [];
    private readonly List<string> _warnings = [];      // from parsing the headers
    private readonly List<string> _scanWarnings = [];  // from extracting the scan data
    private readonly QuantizationTable?[] _quantTables = new QuantizationTable?[4];
    private readonly HuffmanTable?[] _dcTables = new HuffmanTable?[4];
    private readonly HuffmanTable?[] _acTables = new HuffmanTable?[4];

    private JpegFile(byte[] data)
    {
        Source = data;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != Markers.SOI)
            throw new JpegFormatException("Not a JPEG file (missing SOI marker).");

        _segments.Add(new JpegSegment(Markers.SOI, 0, 0));

        FrameHeader? frame = null;
        ScanHeader? scan = null;
        int pos = 2;

        while (scan == null)
        {
            if (pos >= data.Length)
                throw new JpegFormatException("File ends before the start of scan (SOS).");

            if (data[pos] != 0xFF)
            {
                int next = Array.IndexOf(data, (byte)0xFF, pos);
                if (next < 0)
                    throw new JpegFormatException($"No more markers after offset {pos}; file ends before the start of scan (SOS).");
                _warnings.Add($"Skipped {next - pos} unexpected byte(s) at offset {pos}.");
                pos = next;
            }

            int markerOffset = pos;
            while (pos < data.Length && data[pos] == 0xFF)
                pos++;
            if (pos >= data.Length)
                throw new JpegFormatException("File ends before the start of scan (SOS).");

            byte marker = data[pos++];

            if (marker == Markers.EOI)
                throw new JpegFormatException($"End of image (EOI) at offset {markerOffset}, before any scan data.");
            if (marker == 0x00 || marker == 0x01 || Markers.IsRst(marker))
            {
                _warnings.Add($"Ignored stray marker {Markers.Name(marker)} at offset {markerOffset}.");
                continue;
            }

            if (pos + 2 > data.Length)
                throw new JpegFormatException($"Truncated {Markers.Name(marker)} segment at offset {markerOffset}.");
            int length = (data[pos] << 8) | data[pos + 1];
            if (length < 2 || pos + length > data.Length)
                throw new JpegFormatException($"Invalid length {length} for {Markers.Name(marker)} segment at offset {markerOffset}.");

            var payload = new ReadOnlySpan<byte>(data, pos + 2, length - 2);
            _segments.Add(new JpegSegment(marker, markerOffset, length));

            switch (marker)
            {
                case Markers.DQT:
                    ParseQuantizationTables(payload, markerOffset);
                    break;
                case Markers.DHT:
                    ParseHuffmanTables(payload, markerOffset);
                    break;
                case Markers.DRI:
                    if (payload.Length < 2)
                        throw new JpegFormatException($"Truncated DRI segment at offset {markerOffset}.");
                    RestartInterval = (payload[0] << 8) | payload[1];
                    break;
                case Markers.SOF0:
                case Markers.SOF1:
                    if (frame != null)
                        throw new JpegFormatException($"Second frame header at offset {markerOffset}.");
                    frame = FrameHeader.Parse(marker, payload);
                    break;
                case Markers.SOS:
                    if (frame == null)
                        throw new JpegFormatException("Start of scan (SOS) before the frame header (SOF).");
                    scan = ScanHeader.Parse(payload, frame);
                    break;
                default:
                    if (Markers.IsSof(marker))
                        throw new JpegNotSupportedException($"{Markers.DescribeFrame(marker)} JPEGs are not supporte.");
                    break; // APPn, COM and anything else is kept verbatim.
            }

            pos += length;
        }

        Frame = frame!;
        Scan = scan;
        ScanDataOffset = pos;
        Entropy = EntropyData.Extract(data, pos);
        UpdateScanWarnings();
    }

    public static JpegFile Parse(byte[] data) => new(data);

    public static JpegFile Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>The original file bytes.</summary>
    public byte[] Source { get; }

    /// <summary>Segments up to and including the first SOS, in file order.</summary>
    public IReadOnlyList<JpegSegment> Segments => _segments;

    /// <summary>Problems the parser worked around (stray bytes, stray markers).</summary>
    public IReadOnlyList<string> Warnings => [.. _warnings, .. _scanWarnings];

    public FrameHeader Frame { get; }

    public ScanHeader Scan { get; }

    /// <summary>Restart interval in MCUs; 0 = no restart markers.</summary>
    public int RestartInterval { get; private set; }

    /// <summary>Offset of the first entropy-coded byte (just after the SOS header).</summary>
    public int ScanDataOffset { get; }

    public EntropyData Entropy { get; private set; }

    /// <summary>
    /// Called when the picture ran out of data before it was complete. If the scan was ended by a marker that has
    /// more real data after it (not another image, not padding), that marker is treated as damaged data and the
    /// scan is extracted again, reading past it. Returns false if there is nothing to continue with.
    /// </summary>
    internal bool TryContinuePastEndMarker()
    {
        if (Entropy.EndMarker == null)
            return false;

        int after = Entropy.SourceEnd + 2;
        if (after >= Source.Length)
            return false;

        var rest = Source.AsSpan(after);
        if (rest.Length >= 2 && rest[0] == 0xFF && rest[1] == Markers.SOI)
            return false; // another image follows (e.g. a multi-picture JPEG): the scan really ended
        if (rest.IndexOfAnyExcept((byte)0x00, (byte)0xFF) < 0)
            return false; // only padding follows

        Entropy = EntropyData.Extract(Source, ScanDataOffset, continuePast: after);
        UpdateScanWarnings();
        return true;
    }

    private void UpdateScanWarnings()
    {
        const int maxListed = 20;
        _scanWarnings.Clear();
        foreach (int offset in Entropy.StrayMarkers.Take(maxListed))
            _scanWarnings.Add($"Damaged data at offset 0x{offset:X}: 0xFF{Source[offset + 1]:X2} inside the scan was read as data, not as a marker.");
        if (Entropy.StrayMarkers.Count > maxListed)
            _scanWarnings.Add($"…and {Entropy.StrayMarkers.Count - maxListed} more damaged marker(s) inside the scan.");
    }

    public QuantizationTable? GetQuantizationTable(int id) => _quantTables[id];

    public HuffmanTable? GetDcTable(int id) => _dcTables[id];

    public HuffmanTable? GetAcTable(int id) => _acTables[id];

    private void ParseQuantizationTables(ReadOnlySpan<byte> p, int offset)
    {
        int i = 0;
        while (i < p.Length)
        {
            int pq = p[i] >> 4, tq = p[i] & 15;
            i++;
            if (pq > 1 || tq > 3)
                throw new JpegFormatException($"Invalid quantization table header in DQT at offset {offset}.");

            int size = pq == 0 ? 64 : 128;
            if (i + size > p.Length)
                throw new JpegFormatException($"Truncated quantization table in DQT at offset {offset}.");

            var natural = new ushort[64];
            for (int k = 0; k < 64; k++)
            {
                natural[ZigZag.ToNatural[k]] = pq == 0
                    ? p[i + k]
                    : (ushort)((p[i + 2 * k] << 8) | p[i + 2 * k + 1]);
            }

            i += size;
            _quantTables[tq] = new QuantizationTable(tq, pq, natural);
        }
    }

    private void ParseHuffmanTables(ReadOnlySpan<byte> p, int offset)
    {
        int i = 0;
        while (i < p.Length)
        {
            if (i + 17 > p.Length)
                throw new JpegFormatException($"Truncated Huffman table in DHT at offset {offset}.");

            int tc = p[i] >> 4, th = p[i] & 15;
            if (tc > 1 || th > 3)
                throw new JpegFormatException($"Invalid Huffman table header in DHT at offset {offset}.");

            var counts = p.Slice(i + 1, 16).ToArray();
            int total = 0;
            foreach (var c in counts) total += c;
            i += 17;

            if (i + total > p.Length)
                throw new JpegFormatException($"Truncated Huffman table in DHT at offset {offset}.");

            var symbols = p.Slice(i, total).ToArray();
            i += total;

            var table = new HuffmanTable(tc, th, counts, symbols);
            if (tc == 0) _dcTables[th] = table;
            else _acTables[th] = table;
        }
    }
}
