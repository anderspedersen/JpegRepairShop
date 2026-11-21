namespace JpegRepair.Core;

/// <param name="ByteOffset">Offset in <see cref="EntropyData.Bytes"/> where the data after the marker starts.</param>
/// <param name="Number">Restart number 0..7 (RSTn).</param>
/// <param name="SourceOffset">Offset of the 0xFF byte of the marker in the original file.</param>
public readonly record struct RestartMarker(int ByteOffset, int Number, int SourceOffset);

/// <summary>
/// The entropy-coded scan data with byte stuffing and restart markers removed, so that bit positions
/// refer to the actual Huffman bit stream. Keeps enough information to map positions back to the file.
/// </summary>
public sealed class EntropyData
{
    // Markers that may legitimately follow a scan (EOI is handled separately).
    private static readonly HashSet<byte> SegmentMarkers = BuildSegmentMarkers();

    // (destuffed index, source offset) pairs at every discontinuity, sorted by destuffed index.
    private readonly List<(int Index, int Source)> _checkpoints;

    private EntropyData(byte[] bytes, List<RestartMarker> restarts, List<int> strayMarkers, List<(int, int)> checkpoints,
        int sourceStart, int sourceEnd, byte? endMarker)
    {
        Bytes = bytes;
        RestartMarkers = restarts;
        StrayMarkers = strayMarkers;
        _checkpoints = checkpoints;
        SourceStart = sourceStart;
        SourceEnd = sourceEnd;
        EndMarker = endMarker;
    }

    public byte[] Bytes { get; }

    public IReadOnlyList<RestartMarker> RestartMarkers { get; }

    /// <summary>
    /// File offsets of 0xFF xx pairs inside the scan that are not valid there and were treated as damaged data
    /// (kept as an 0xFF data byte) instead of ending the scan.
    /// </summary>
    public IReadOnlyList<int> StrayMarkers { get; }

    /// <summary>Offset in the file of the first entropy-coded byte.</summary>
    public int SourceStart { get; }

    /// <summary>Offset in the file of the marker that ended the scan, or the file length if the file is truncated.</summary>
    public int SourceEnd { get; }

    /// <summary>The marker that ended the scan (normally EOI), or null if the file ended first.</summary>
    public byte? EndMarker { get; }

    /// <summary>Maps an offset in <see cref="Bytes"/> to an offset in the original file.</summary>
    public int ToSourceOffset(int byteIndex)
    {
        int lo = 0, hi = _checkpoints.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_checkpoints[mid].Index <= byteIndex) lo = mid;
            else hi = mid - 1;
        }

        var cp = _checkpoints[lo];
        return cp.Source + (byteIndex - cp.Index);
    }

    /// <param name="continuePast">Markers before this file offset never end the scan (they are treated as damaged data).</param>
    internal static EntropyData Extract(byte[] file, int start, int continuePast = 0)
    {
        var bytes = new byte[Math.Max(0, file.Length - start)];
        var restarts = new List<RestartMarker>();
        var stray = new List<int>();
        var checkpoints = new List<(int, int)> { (0, start) };
        int n = 0, i = start;
        byte? endMarker = null;

        while (i < file.Length)
        {
            byte b = file[i];
            if (b != 0xFF)
            {
                bytes[n++] = b;
                i++;
                continue;
            }

            if (i + 1 >= file.Length)
            {
                // A lone 0xFF as the very last byte of a truncated file.
                bytes[n++] = b;
                i++;
                break;
            }

            byte next = file[i + 1];
            if (next == 0x00)
            {
                bytes[n++] = 0xFF;
                i += 2;
                checkpoints.Add((n, i));
            }
            else if (next == 0xFF)
            {
                i++; // fill byte
                checkpoints.Add((n, i));
            }
            else if (Markers.IsRst(next))
            {
                restarts.Add(new RestartMarker(n, next - Markers.RST0, i));
                i += 2;
                checkpoints.Add((n, i));
            }
            else if (i >= continuePast && IsEndOfScan(file, i))
            {
                endMarker = next;
                break;
            }
            else
            {
                // 0xFF followed by something that can't be here: damaged data (often a flipped stuffing byte,
                // or foreign data written over the file). Keep going instead of cutting the picture off.
                stray.Add(i);
                bytes[n++] = 0xFF;
                i += 2;
                checkpoints.Add((n, i));
            }
        }

        Array.Resize(ref bytes, n);
        return new EntropyData(bytes, restarts, stray, checkpoints, start, i, endMarker);
    }

    /// <summary>
    /// True if the marker at <paramref name="i"/> really ends the scan: EOI, or a segment marker whose length
    /// field leads to the next marker (or to the end of the file).
    /// </summary>
    private static bool IsEndOfScan(byte[] file, int i)
    {
        byte marker = file[i + 1];
        if (marker == Markers.EOI)
            return true;
        if (!SegmentMarkers.Contains(marker) || i + 4 > file.Length)
            return false;

        int length = (file[i + 2] << 8) | file[i + 3];
        int next = i + 2 + length;
        return length >= 2 && (next == file.Length || (next + 1 < file.Length && file[next] == 0xFF));
    }

    private static HashSet<byte> BuildSegmentMarkers()
    {
        var set = new HashSet<byte> { Markers.DHT, Markers.DQT, Markers.DRI, Markers.SOS, Markers.DNL, Markers.COM };
        for (int m = Markers.APP0; m <= Markers.APP15; m++)
            set.Add((byte)m);
        for (int m = 0xC0; m <= 0xCF; m++)
        {
            if (Markers.IsSof((byte)m))
                set.Add((byte)m);
        }

        return set;
    }
}
