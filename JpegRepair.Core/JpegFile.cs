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
/// <remarks>
/// The segment chain is followed from the start of the file. If that fails, or it leads to a thumbnail or
/// preview instead of the main picture, the header is taken from a <see cref="MarkerScan"/> of the whole file.
/// </remarks>
public sealed class JpegFile
{
    private readonly List<JpegSegment> _segments;
    private readonly List<string> _warnings;           // from parsing the headers
    private readonly List<string> _scanWarnings = [];  // from extracting the scan data
    private readonly QuantizationTable?[] _quantTables;
    private readonly HuffmanTable?[] _dcTables;
    private readonly HuffmanTable?[] _acTables;

    private JpegFile(byte[] data)
    {
        Source = data;
        var header = ReadHeader(data);

        _segments = header.Segments;
        _warnings = header.Warnings;
        _quantTables = header.QuantTables;
        _dcTables = header.DcTables;
        _acTables = header.AcTables;
        Frame = header.Frame!;
        Scan = header.Scan!;
        RestartInterval = header.RestartInterval;
        ScanDataOffset = header.ScanDataOffset;
        Header = header.Bytes ?? data.AsMemory(0, header.ScanDataOffset);
        Entropy = EntropyData.Extract(data, ScanDataOffset);
        UpdateScanWarnings();
    }

    public static JpegFile Parse(byte[] data) => new(data);

    public static JpegFile Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>The original file bytes.</summary>
    public byte[] Source { get; }

    /// <summary>The header segments that were used, up to and including the SOS, with their offsets in <see cref="Source"/>.</summary>
    public IReadOnlyList<JpegSegment> Segments => _segments;

    /// <summary>Problems the parser worked around (stray bytes, stray markers, a header found by searching).</summary>
    public IReadOnlyList<string> Warnings => [.. _warnings, .. _scanWarnings];

    public FrameHeader Frame { get; }

    public ScanHeader Scan { get; }

    /// <summary>Restart interval in MCUs; 0 = no restart markers.</summary>
    public int RestartInterval { get; }

    /// <summary>Offset of the first entropy-coded byte (just after the SOS header) in <see cref="Source"/>.</summary>
    public int ScanDataOffset { get; }

    /// <summary>
    /// The bytes to write in front of the scan data: normally the file's own bytes up to <see cref="ScanDataOffset"/>;
    /// a picture that starts later in the file, or a header rebuilt from its tables, frame and scan headers, if the
    /// start of the file is damaged.
    /// </summary>
    public ReadOnlyMemory<byte> Header { get; }

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

    private static HeaderRead ReadHeader(byte[] data)
    {
        HeaderRead chain;
        try
        {
            chain = HeaderRead.FollowChain(data, 0);
        }
        catch (JpegFormatException ex)
        {
            // "Not supported" from the chain stands, unless the marker search finds another picture later in the
            // file: then the chain read garbage (e.g. an encrypted start of the file) as a frame header.
            var main = MarkerScan.Run(data).Main;
            if (main == null || (ex is JpegNotSupportedException && main.SoiOffset is null or 0))
                throw;

            HeaderRead found;
            try
            {
                found = HeaderRead.FromMarkerScan(data, main);
            }
            catch (JpegFormatException inner) when (inner is not JpegNotSupportedException)
            {
                throw new JpegFormatException(
                    $"{ex.Message} A search for markers found image data at offset {main.ScanDataOffset}, but its header is damaged too: {inner.Message}");
            }

            found.Warnings.Insert(0, $"The header is damaged: {ex.Message} The picture's header was found by searching for markers " +
                                     $"({Describe(main)}).");
            return found;
        }

        // The chain worked, but a damaged length field can make it land inside the EXIF data and pick up a
        // thumbnail's or preview's header instead of the main picture's.
        var scan = MarkerScan.Run(data);
        var main2 = scan.Main;
        var reached = scan.Images.FirstOrDefault(i => i.ScanDataOffset == chain.ScanDataOffset);
        if (main2 == null || reached == null || reached == main2 || reached.ScanDataOffset > main2.ScanDataOffset)
            return chain;

        HeaderRead better;
        try
        {
            better = HeaderRead.FromMarkerScan(data, main2);
        }
        catch (JpegFormatException ex) when (ex is not JpegNotSupportedException)
        {
            throw new JpegFormatException(
                $"The header leads to an embedded preview ({Describe(reached)}), not to the main picture " +
                $"({Describe(main2)}), and the main picture's header is damaged: {ex.Message}");
        }

        better.Warnings.Insert(0, $"The header led to an embedded preview ({Describe(reached)}); " +
                                  $"the main picture's header was found by searching for markers ({Describe(main2)}).");
        return better;
    }

    private static string Describe(EmbeddedImage image) =>
        (image.Frame != null ? $"{image.Width}x{image.Height}, " : "") + $"image data at offset {image.ScanDataOffset}";

    /// <summary>One attempt at reading a header: its segments, tables, frame and scan header.</summary>
    private sealed class HeaderRead
    {
        public readonly List<JpegSegment> Segments = [];
        public readonly List<string> Warnings = [];
        public readonly QuantizationTable?[] QuantTables = new QuantizationTable?[4];
        public readonly HuffmanTable?[] DcTables = new HuffmanTable?[4];
        public readonly HuffmanTable?[] AcTables = new HuffmanTable?[4];
        public FrameHeader? Frame;
        public ScanHeader? Scan;
        public int RestartInterval;
        public int ScanDataOffset;

        /// <summary>The header to write, when it is not the file's bytes before the scan data.</summary>
        public byte[]? Bytes;

        /// <summary>Follows the segment chain from the SOI at <paramref name="start"/> to the first SOS.</summary>
        public static HeaderRead FollowChain(byte[] data, int start)
        {
            if (data.Length < start + 4 || data[start] != 0xFF || data[start + 1] != Markers.SOI)
                throw new JpegFormatException("Not a JPEG file (missing SOI marker).");

            var h = new HeaderRead();
            h.Segments.Add(new JpegSegment(Markers.SOI, start, 0));
            int pos = start + 2;

            while (h.Scan == null)
            {
                if (pos >= data.Length)
                    throw new JpegFormatException("File ends before the start of scan (SOS).");

                if (data[pos] != 0xFF)
                {
                    int next = Array.IndexOf(data, (byte)0xFF, pos);
                    if (next < 0)
                        throw new JpegFormatException($"No more markers after offset {pos}; file ends before the start of scan (SOS).");
                    h.Warnings.Add($"Skipped {next - pos} unexpected byte(s) at offset {pos}.");
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
                    h.Warnings.Add($"Ignored stray marker {Markers.Name(marker)} at offset {markerOffset}.");
                    continue;
                }

                if (pos + 2 > data.Length)
                    throw new JpegFormatException($"Truncated {Markers.Name(marker)} segment at offset {markerOffset}.");
                int length = (data[pos] << 8) | data[pos + 1];
                if (length < 2 || pos + length > data.Length)
                    throw new JpegFormatException($"Invalid length {length} for {Markers.Name(marker)} segment at offset {markerOffset}.");

                h.Read(data, new JpegSegment(marker, markerOffset, length), pos + 2);
                pos += length;
            }

            h.ScanDataOffset = pos;
            return h;
        }

        /// <summary>
        /// Reads the header of a picture found by <see cref="MarkerScan"/>. If the picture has its own SOI later in
        /// the file and the chain from there reaches the same scan, that part of the file is the header (metadata
        /// included); otherwise the header is rebuilt from the picture's tables, frame header and scan header.
        /// </summary>
        public static HeaderRead FromMarkerScan(byte[] data, EmbeddedImage image)
        {
            if (image.SoiOffset is int soi && soi > 0)
            {
                try
                {
                    var chain = FollowChain(data, soi);
                    if (chain.ScanDataOffset == image.ScanDataOffset)
                    {
                        chain.Bytes = data[soi..chain.ScanDataOffset];
                        chain.Warnings.Insert(0, $"The picture starts at offset {soi}; the bytes before it were left out.");
                        return chain;
                    }
                }
                catch (JpegFormatException ex) when (ex is not JpegNotSupportedException)
                {
                    // The picture's own chain is damaged too: rebuild below.
                }
            }

            if (image.Frame == null)
                throw new JpegFormatException("No frame header (SOF) was found before the image data.");

            var h = new HeaderRead();
            if (image.SoiOffset is { } start)
                h.Segments.Add(new JpegSegment(Markers.SOI, start, 0));

            var parts = image.Tables.Append(image.Frame).OrderBy(s => s.Offset).Append(image.FirstScan).ToList();
            using var bytes = new MemoryStream();
            bytes.WriteByte(0xFF);
            bytes.WriteByte(Markers.SOI);
            foreach (var segment in parts)
            {
                h.Read(data, segment, segment.Offset + 4);
                bytes.Write(data, segment.Offset, 2 + segment.Length);
            }

            h.CheckTables();
            h.ScanDataOffset = image.ScanDataOffset;
            h.Bytes = bytes.ToArray();
            h.Warnings.Add("The header was rebuilt from the picture's tables, frame header and scan header; EXIF and other metadata were left out.");
            return h;
        }

        private void Read(byte[] data, JpegSegment segment, int payloadStart)
        {
            var payload = new ReadOnlySpan<byte>(data, payloadStart, segment.Length - 2);
            Segments.Add(segment);

            switch (segment.Marker)
            {
                case Markers.DQT:
                    ParseQuantizationTables(payload, segment.Offset);
                    break;
                case Markers.DHT:
                    ParseHuffmanTables(payload, segment.Offset);
                    break;
                case Markers.DRI:
                    if (payload.Length < 2)
                        throw new JpegFormatException($"Truncated DRI segment at offset {segment.Offset}.");
                    RestartInterval = (payload[0] << 8) | payload[1];
                    break;
                case Markers.SOF0:
                case Markers.SOF1:
                    if (Frame != null)
                        throw new JpegFormatException($"Second frame header at offset {segment.Offset}.");
                    Frame = FrameHeader.Parse(segment.Marker, payload);
                    break;
                case Markers.SOS:
                    if (Frame == null)
                        throw new JpegFormatException("Start of scan (SOS) before the frame header (SOF).");
                    Scan = ScanHeader.Parse(payload, Frame);
                    break;
                default:
                    if (Markers.IsSof(segment.Marker))
                        throw new JpegNotSupportedException($"{Markers.DescribeFrame(segment.Marker)} JPEGs are not supported.");
                    break; // APPn, COM and anything else is kept verbatim.
            }
        }

        /// <summary>Every table the frame and scan refer to must be present.</summary>
        private void CheckTables()
        {
            foreach (var c in Frame!.Components)
            {
                if (QuantTables[c.QuantTableId] == null)
                    throw new JpegFormatException($"Missing quantization table {c.QuantTableId} for component {c.Id}.");
            }

            foreach (var sc in Scan!.Components)
            {
                if (DcTables[sc.DcTableId] == null)
                    throw new JpegFormatException($"Missing DC Huffman table {sc.DcTableId} for component {sc.Component.Id}.");
                if (AcTables[sc.AcTableId] == null)
                    throw new JpegFormatException($"Missing AC Huffman table {sc.AcTableId} for component {sc.Component.Id}.");
            }
        }

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
                QuantTables[tq] = new QuantizationTable(tq, pq, natural);
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
                if (tc == 0) DcTables[th] = table;
                else AcTables[th] = table;
            }
        }
    }
}
