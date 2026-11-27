namespace JpegRepair.Core;

/// <summary>
/// One picture found by <see cref="MarkerScan"/>: a scan header with the frame header and tables before it.
/// Consecutive scans that share a frame header (progressive pictures) count as one picture.
/// </summary>
public sealed class EmbeddedImage
{
    internal EmbeddedImage(int? soiOffset, JpegSegment? frame, JpegSegment firstScan, IReadOnlyList<JpegSegment> tables,
                           int width, int height)
    {
        SoiOffset = soiOffset;
        Frame = frame;
        FirstScan = firstScan;
        Tables = tables;
        Width = width;
        Height = height;
    }

    /// <summary>Offset of the SOI marker that starts the picture, or null if none was found before it.</summary>
    public int? SoiOffset { get; }

    /// <summary>The frame header (SOFn), or null if none was found before the first scan.</summary>
    public JpegSegment? Frame { get; }

    /// <summary>The first scan header (SOS).</summary>
    public JpegSegment FirstScan { get; }

    /// <summary>DQT, DHT and DRI segments between the picture's SOI and its first scan, in file order.</summary>
    public IReadOnlyList<JpegSegment> Tables { get; }

    /// <summary>Frame size from the frame header; 0 if there is none.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Offset of the first entropy-coded byte (just after the first SOS header).</summary>
    public int ScanDataOffset => FirstScan.Offset + 2 + FirstScan.Length;

    /// <summary>Offset of the marker that ends the last scan (EOI or a table/scan header), or the file length.</summary>
    public int ScanEnd { get; internal set; }

    /// <summary>The marker at <see cref="ScanEnd"/>; null if the data runs to the end of the file.</summary>
    public byte? EndMarker { get; internal set; }

    public int ScanCount { get; internal set; } = 1;

    public int ScanDataLength => ScanEnd - ScanDataOffset;

    /// <summary>True for a picture that starts and ends inside an EXIF segment (APP1): a thumbnail or preview.</summary>
    public bool InsideExif { get; internal set; }
}

/// <summary>
/// Finds every picture in a file by searching for markers instead of following the segment chain, so a damaged
/// length field or a damaged start of the file doesn't hide the headers. Camera files hold several pictures:
/// the EXIF thumbnail and maker-note previews inside APP1, sometimes a large preview after the main picture's EOI,
/// and the main picture itself.
/// </summary>
public sealed class MarkerScan
{
    private MarkerScan(IReadOnlyList<JpegSegment> segments, IReadOnlyList<EmbeddedImage> images, EmbeddedImage? main)
    {
        Segments = segments;
        Images = images;
        Main = main;
    }

    /// <summary>
    /// Every SOI and EOI marker and every segment whose length fits the file and whose content is valid
    /// (table structure for DQT/DHT, field ranges for SOF/SOS/DRI; APPn and COM are only checked for length).
    /// </summary>
    public IReadOnlyList<JpegSegment> Segments { get; }

    /// <summary>The pictures in file order.</summary>
    public IReadOnlyList<EmbeddedImage> Images { get; }

    /// <summary>The picture with the most scan data that is not a thumbnail or preview inside the EXIF data; null if none.</summary>
    public EmbeddedImage? Main { get; }

    public static MarkerScan Run(byte[] data)
    {
        var segments = FindSegments(data);
        var images = GroupImages(data, segments);
        MarkExifPreviews(segments, images);

        EmbeddedImage? main = null;
        foreach (var image in images)
        {
            if (!image.InsideExif && (main == null || image.ScanDataLength > main.ScanDataLength))
                main = image;
        }

        return new MarkerScan(segments, images, main);
    }

    private static List<JpegSegment> FindSegments(byte[] data)
    {
        var segments = new List<JpegSegment>();
        int i = 0;
        while (true)
        {
            i = Array.IndexOf(data, (byte)0xFF, i);
            if (i < 0 || i + 1 >= data.Length)
                break;

            byte marker = data[i + 1];
            if (marker is Markers.SOI or Markers.EOI)
                segments.Add(new JpegSegment(marker, i, 0));
            else if (TryValidate(data, i, out int length))
                segments.Add(new JpegSegment(marker, i, length));
            i++;
        }

        return segments;
    }

    private static bool TryValidate(byte[] data, int offset, out int length)
    {
        length = 0;
        byte marker = data[offset + 1];
        bool hasLength = marker is Markers.DHT or Markers.SOS or Markers.DQT or Markers.DRI or Markers.COM
                             or >= Markers.APP0 and <= Markers.APP15
                         || Markers.IsSof(marker);
        if (!hasLength || offset + 4 > data.Length)
            return false;

        length = (data[offset + 2] << 8) | data[offset + 3];
        if (length < 2 || offset + 2 + length > data.Length)
            return false;

        var p = data.AsSpan(offset + 4, length - 2);
        return marker switch
        {
            Markers.DQT => IsValidDqt(p),
            Markers.DHT => IsValidDht(p),
            Markers.SOS => p.Length >= 1 && p[0] is >= 1 and <= 4 && length == 6 + 2 * p[0],
            Markers.DRI => length == 4,
            _ when Markers.IsSof(marker) => IsValidSof(p, length),
            _ => true,
        };
    }

    private static bool IsValidDqt(ReadOnlySpan<byte> p)
    {
        if (p.IsEmpty)
            return false;
        int i = 0;
        while (i < p.Length)
        {
            int pq = p[i] >> 4, tq = p[i] & 15;
            if (pq > 1 || tq > 3)
                return false;
            i += 1 + 64 * (pq + 1);
        }

        return i == p.Length;
    }

    private static bool IsValidDht(ReadOnlySpan<byte> p)
    {
        if (p.IsEmpty)
            return false;
        int i = 0;
        while (i < p.Length)
        {
            if (i + 17 > p.Length || p[i] >> 4 > 1 || (p[i] & 15) > 3)
                return false;
            int total = 0;
            for (int k = 1; k <= 16; k++)
                total += p[i + k];
            if (total is 0 or > 256)
                return false;
            i += 17 + total;
        }

        return i == p.Length;
    }

    private static bool IsValidSof(ReadOnlySpan<byte> p, int length)
    {
        if (p.Length < 6)
            return false;
        int count = p[5];
        if (p[0] is not (8 or 12) || count is not (1 or 3 or 4) || length != 8 + 3 * count)
            return false;
        if (((p[3] << 8) | p[4]) == 0)
            return false;
        for (int c = 0; c < count; c++)
        {
            int sampling = p[7 + 3 * c];
            if (sampling >> 4 is < 1 or > 4 || (sampling & 15) is < 1 or > 4 || p[8 + 3 * c] > 3)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Walks the scan headers in file order. Each picture claims the latest SOI, frame header and tables before
    /// its scan that are not inside a picture already closed by its EOI, so a thumbnail's tables are never
    /// taken for the main picture's.
    /// </summary>
    private static List<EmbeddedImage> GroupImages(byte[] data, List<JpegSegment> segments)
    {
        var images = new List<EmbeddedImage>();
        var closed = new List<(int Start, int End)>();

        // Only these can follow a scan; APPn or SOF "segments" found inside scan data are chance hits.
        var scanEnders = new HashSet<int>();
        foreach (var s in segments)
        {
            if (s.Marker is Markers.DQT or Markers.DHT or Markers.DRI or Markers.SOS)
                scanEnders.Add(s.Offset);
        }

        bool Inside(int offset)
        {
            foreach (var (start, end) in closed)
            {
                if (offset >= start && offset < end)
                    return true;
            }

            return false;
        }

        foreach (var sos in segments)
        {
            if (sos.Marker != Markers.SOS || Inside(sos.Offset))
                continue;

            int? soi = null;
            foreach (var s in segments)
            {
                if (s.Offset >= sos.Offset)
                    break;
                if (s.Marker == Markers.SOI && !Inside(s.Offset))
                    soi = s.Offset;
            }

            int from = soi ?? 0;
            JpegSegment? frame = null;
            var tables = new List<JpegSegment>();
            foreach (var s in segments)
            {
                if (s.Offset >= sos.Offset)
                    break;
                if (s.Offset < from || Inside(s.Offset))
                    continue;
                if (Markers.IsSof(s.Marker))
                    frame = s;
                else if (s.Marker is Markers.DQT or Markers.DHT or Markers.DRI)
                    tables.Add(s);
            }

            var (end, endMarker) = FindScanEnd(data, sos.Offset + 2 + sos.Length, scanEnders);

            EmbeddedImage image;
            var previous = images.Count > 0 ? images[^1] : null;
            if (previous != null && frame != null && previous.Frame == frame && previous.SoiOffset == soi)
            {
                image = previous;
                image.ScanCount++;
            }
            else
            {
                int width = 0, height = 0;
                if (frame != null)
                {
                    height = (data[frame.Offset + 5] << 8) | data[frame.Offset + 6];
                    width = (data[frame.Offset + 7] << 8) | data[frame.Offset + 8];
                }

                image = new EmbeddedImage(soi, frame, sos, tables, width, height);
                images.Add(image);
            }

            image.ScanEnd = end;
            image.EndMarker = endMarker;
            if (endMarker == Markers.EOI && soi != null)
                closed.Add((soi.Value, end + 2));
        }

        return images;
    }

    /// <summary>The first EOI or table/scan header after the scan data; other 0xFFxx pairs are damaged scan data.</summary>
    private static (int End, byte? Marker) FindScanEnd(byte[] data, int start, HashSet<int> scanEnders)
    {
        int i = start;
        while (true)
        {
            i = Array.IndexOf(data, (byte)0xFF, i);
            if (i < 0 || i + 1 >= data.Length)
                return (data.Length, null);
            byte marker = data[i + 1];
            if (marker == Markers.EOI || scanEnders.Contains(i))
                return (i, marker);
            i++;
        }
    }

    private static void MarkExifPreviews(List<JpegSegment> segments, List<EmbeddedImage> images)
    {
        // An EXIF segment is an APP1 that directly follows an SOI or another APPn segment.
        var exif = new List<(int Start, int End)>();
        for (int k = 1; k < segments.Count; k++)
        {
            var app1 = segments[k];
            var previous = segments[k - 1];
            if (app1.Marker != Markers.APP0 + 1)
                continue;
            bool follows = previous.Marker == Markers.SOI
                ? previous.Offset + 2 == app1.Offset
                : previous.Marker is >= Markers.APP0 and <= Markers.APP15 && previous.Offset + 2 + previous.Length == app1.Offset;
            if (follows)
                exif.Add((app1.Offset, app1.Offset + 2 + app1.Length));
        }

        foreach (var image in images)
        {
            image.InsideExif = image.SoiOffset is { } soi && image.EndMarker == Markers.EOI
                               && exif.Any(e => soi > e.Start && soi < e.End && image.ScanEnd < e.End);
        }
    }
}
