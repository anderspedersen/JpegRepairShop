namespace JpegRepair.Core;

public enum McuStatus : byte
{
    /// <summary>Never decoded: the data ended, or it was skipped while resynchronising.</summary>
    Missing = 0,

    Ok = 1,

    /// <summary>Decoding hit invalid data inside this MCU. Its coefficients are whatever was read before the error.</summary>
    Corrupt = 2,

    /// <summary>An empty (flat grey) MCU inserted by an edit.</summary>
    Blank = 3,

    /// <summary>Contents made up from the picture around it by an edit (see <see cref="JpegDocument.PrepareFill"/>).</summary>
    Filled = 4,
}

public struct McuInfo
{
    /// <summary>Bit position in <see cref="EntropyData.Bytes"/> where this MCU starts.</summary>
    public long BitOffset;

    /// <summary>Number of bits this MCU used.</summary>
    public int BitLength;

    public McuStatus Status;
}

public readonly record struct DecodeIssue(int McuIndex, long BitOffset, string Message);

public sealed class ScanDecodeResult : IMcuSource
{
    internal ScanDecodeResult(McuLayout layout, short[] coefficients, McuInfo[] mcus, List<DecodeIssue> issues, long bitsUsed)
    {
        Layout = layout;
        Coefficients = coefficients;
        Mcus = mcus;
        Issues = issues;
        BitsUsed = bitsUsed;
    }

    public McuLayout Layout { get; }

    /// <summary>
    /// Quantized coefficients for all MCUs: MCU-major, then block (in <see cref="McuLayout.Blocks"/> order),
    /// then 64 values in natural order. DC values are absolute (prediction already applied).
    /// </summary>
    public short[] Coefficients { get; }

    /// <summary>
    /// One entry per MCU of the frame, followed by any overflow MCUs: MCUs decoded from data left over after
    /// the frame was full. Overflow appears when garbage was inserted earlier in the scan; removing the bad
    /// MCUs in the editor pulls the overflow back into the picture.
    /// </summary>
    public McuInfo[] Mcus { get; }

    public int OverflowCount => Mcus.Length - Layout.Frame.McuCount;

    public IReadOnlyList<DecodeIssue> Issues { get; }

    int IMcuSource.Count => Mcus.Length;

    McuStatus IMcuSource.GetStatus(int index) => Mcus[index].Status;

    ReadOnlySpan<short> IMcuSource.GetCoefficients(int index) => GetMcuCoefficients(index);

    /// <summary>Bit position where decoding stopped.</summary>
    public long BitsUsed { get; }

    public int CountWithStatus(McuStatus status) => Mcus.Count(m => m.Status == status);

    public Span<short> GetMcuCoefficients(int mcu) =>
        Coefficients.AsSpan(mcu * Layout.CoefficientsPerMcu, Layout.CoefficientsPerMcu);

    public Span<short> GetBlock(int mcu, int block) =>
        Coefficients.AsSpan((mcu * Layout.BlocksPerMcu + block) * 64, 64);
}

/// <summary>
/// Decodes a baseline Huffman scan into quantized coefficients. Never throws on corrupt scan data.
///
/// Like the original C++ tool, it keeps going after invalid data instead of stopping: an MCU with an error is
/// marked <see cref="McuStatus.Corrupt"/> and decoding continues with the next MCU from wherever the bits ended up.
/// Huffman streams usually fall back into step within a few MCUs, so the rest of the picture appears (often
/// shifted or with a colour shift), and the repair tools can fix that. <see cref="McuStatus.Missing"/> only
/// remains where data is really gone.
///
/// Restart markers split the data into segments. Markers are checked against their neighbours, so a marker
/// with a damaged number is still used, a marker that is really damaged data is ignored, and the data of an
/// interval whose marker was lost is decoded rather than skipped. If the data runs out before the picture is
/// complete and the scan was ended by a marker with more data after it, that marker is treated as damaged
/// data and decoding is repeated, reading past it.
/// </summary>
public static class ScanDecoder
{
    // Quantized DC/AC ranges that 8-bit baseline JPEG can represent (and our encoder can write back).
    private const int MinDc = -1024;
    private const int MaxDc = 1023;
    private const int MaxAc = 1023;

    // Damaged files can produce thousands of errors; list the first ones and summarize the rest.
    private const int MaxReportedIssues = 200;

    /// <summary>A stretch of entropy-coded data between restart markers and the MCUs it holds.</summary>
    private readonly record struct Segment(int StartByte, int EndByte, int FirstMcu, int EndMcu);

    public static ScanDecodeResult Decode(JpegFile file)
    {
        while (true)
        {
            var result = DecodeOnce(file, out bool ranOut);
            if (!ranOut || !file.TryContinuePastEndMarker())
                return result;
        }
    }

    private static ScanDecodeResult DecodeOnce(JpegFile file, out bool ranOut)
    {
        var frame = file.Frame;
        var layout = new McuLayout(frame, file.Scan);
        var (dc, ac) = layout.ResolveHuffmanTables(file);

        int mcuCount = frame.McuCount;
        int perMcu = layout.CoefficientsPerMcu;
        var coefficients = new short[(long)mcuCount * perMcu];
        var mcus = new McuInfo[mcuCount];
        var issues = new List<DecodeIssue>();
        int suppressed = 0;

        void Report(int mcu, long bit, string message)
        {
            if (issues.Count < MaxReportedIssues)
                issues.Add(new DecodeIssue(mcu, bit, message));
            else
                suppressed++;
        }

        var entropy = file.Entropy;
        var reader = new BitReader(entropy.Bytes);
        var predictors = new int[frame.Components.Count];
        int interval = file.RestartInterval;
        List<Segment> segments = interval > 0
            ? BuildSegments(entropy, interval, mcuCount, Report)
            : new List<Segment> { new(0, entropy.Bytes.Length, 0, mcuCount) };

        ranOut = false;
        bool frameComplete = false;
        for (int s = 0; s < segments.Count; s++)
        {
            var segment = segments[s];
            bool lastSegment = s == segments.Count - 1;
            int first = segment.FirstMcu;
            int end = Math.Min(segment.EndMcu, mcuCount);
            long limit = (long)segment.EndByte * 8;

            if (first >= mcuCount)
            {
                Report(mcuCount, (long)segment.StartByte * 8,
                    $"Restart interval data after the last MCU (file offset 0x{entropy.ToSourceOffset(segment.StartByte):X}).");
                continue;
            }

            reader.Position = (long)segment.StartByte * 8;
            Array.Clear(predictors);

            int m = first;
            while (m < end)
            {
                if (m > first && interval > 0 && m % interval == 0)
                {
                    // A restart marker between these intervals was lost; the encoder still restarted here.
                    Array.Clear(predictors);
                    reader.AlignToByte();
                }

                if (reader.Position >= limit)
                {
                    if (lastSegment)
                    {
                        ranOut = true;
                        Report(m, reader.Position, $"Scan data ends after {m} of {mcuCount} MCUs.");
                    }
                    else
                    {
                        Report(m, reader.Position, $"Restart interval ends early: MCUs {m}–{end - 1} not decoded.");
                    }

                    break;
                }

                long start = reader.Position;
                string? error = DecodeMcu(reader, layout, dc, ac, coefficients.AsSpan(m * perMcu, perMcu), predictors);
                if (reader.Position > limit)
                {
                    error ??= lastSegment ? "scan data ends in the middle of this MCU." : "runs into the next restart marker.";
                    if (lastSegment)
                        ranOut = true;
                }

                mcus[m] = new McuInfo
                {
                    BitOffset = start,
                    BitLength = (int)(reader.Position - start),
                    Status = error == null ? McuStatus.Ok : McuStatus.Corrupt,
                };

                if (error != null)
                    Report(m, start, $"MCU {m}: {error}");
                m++;
            }

            if (lastSegment)
            {
                frameComplete = m >= mcuCount;
            }
            else if (m >= end)
            {
                long extra = segment.EndByte - ((reader.Position + 7) >> 3);
                if (extra > 0)
                    Report(end - 1, reader.Position, $"{extra} unexpected byte(s) before the restart marker after MCU {end - 1}.");
            }
        }

        if (frameComplete)
        {
            long endOfFrame = reader.Position;
            long leftover = entropy.Bytes.Length - ((endOfFrame + 7) >> 3);
            if (leftover > 1)
            {
                // Keep decoding past the end of the frame (only without restart markers, where the stream has
                // no fixed structure). These MCUs become visible when bad MCUs are removed in the editor.
                var extra = new List<(short[] Coefficients, McuInfo Info)>();
                if (interval == 0)
                {
                    var buffer = new short[perMcu];
                    while (extra.Count < mcuCount && reader.Position < reader.Length)
                    {
                        long start = reader.Position;
                        string? error = DecodeMcu(reader, layout, dc, ac, buffer, predictors);
                        if (reader.IsPastEnd)
                            break;
                        extra.Add((buffer.ToArray(), new McuInfo
                        {
                            BitOffset = start,
                            BitLength = (int)(reader.Position - start),
                            Status = error == null ? McuStatus.Ok : McuStatus.Corrupt,
                        }));
                    }
                }

                if (extra.Count > 0)
                {
                    Array.Resize(ref coefficients, (mcuCount + extra.Count) * perMcu);
                    Array.Resize(ref mcus, mcuCount + extra.Count);
                    for (int i = 0; i < extra.Count; i++)
                    {
                        extra[i].Coefficients.CopyTo(coefficients, (mcuCount + i) * perMcu);
                        mcus[mcuCount + i] = extra[i].Info;
                    }

                    Report(mcuCount, endOfFrame,
                        $"{leftover} byte(s) of scan data after the last MCU; {extra.Count} extra MCU(s) decoded from them. " +
                        "Data was probably inserted earlier in the file: remove the bad MCUs to pull these back in.");
                }
                else
                {
                    Report(mcuCount, endOfFrame, $"{leftover} byte(s) of scan data left over after the last MCU.");
                }
            }
        }

        if (suppressed > 0)
            issues.Add(new DecodeIssue(-1, 0, $"…and {suppressed} more problem(s) not listed."));

        return new ScanDecodeResult(layout, coefficients, mcus, issues, reader.Position);
    }

    /// <summary>
    /// Splits the data at the restart markers that fit the RST0..RST7 sequence. A marker whose number doesn't fit is
    /// checked against the next one: if the next one fits the sequence without it, it was damaged data and is ignored;
    /// if the next one fits as if it had the expected number, only its number was damaged. Otherwise markers were
    /// lost, and the segment before it holds several intervals.
    /// </summary>
    private static List<Segment> BuildSegments(EntropyData entropy, int interval, int mcuCount, Action<int, long, string> report)
    {
        var markers = entropy.RestartMarkers;
        var segments = new List<Segment>(markers.Count + 1);
        int last = 7;          // number of the previous accepted marker (7, so that RST0 comes first)
        int index = 0;         // interval that starts after the previous accepted marker
        int startByte = 0;
        int firstInterval = 0;

        for (int i = 0; i < markers.Count; i++)
        {
            var marker = markers[i];
            int step = (marker.Number - last) & 7;
            int mcu = Math.Min(index * interval, mcuCount - 1);
            long bit = (long)marker.ByteOffset * 8;

            if (step != 1)
            {
                int? nextStep = i + 1 < markers.Count ? (markers[i + 1].Number - last) & 7 : null;
                if (nextStep == 1 || step == 0)
                {
                    report(mcu, bit, $"Ignored restart marker RST{marker.Number} at file offset 0x{marker.SourceOffset:X}: " +
                                     "it doesn't fit the sequence, so it is probably damaged data.");
                    continue;
                }

                if (nextStep == 2)
                {
                    report(mcu, bit, $"Restart marker at file offset 0x{marker.SourceOffset:X} has a damaged number " +
                                     $"(RST{marker.Number}, expected RST{(last + 1) & 7}).");
                    step = 1;
                }
                else
                {
                    report(mcu, bit, $"{step - 1} restart marker(s) missing before file offset 0x{marker.SourceOffset:X}; " +
                                     "those intervals are decoded without them.");
                }
            }

            index += step;
            last = (last + step) & 7;
            segments.Add(new Segment(startByte, marker.ByteOffset, firstInterval * interval, index * interval));
            startByte = marker.ByteOffset;
            firstInterval = index;
        }

        segments.Add(new Segment(startByte, entropy.Bytes.Length, firstInterval * interval, mcuCount));
        return segments;
    }

    /// <summary>
    /// Decodes one MCU into <paramref name="dest"/>. Always reads the whole MCU, working around invalid data
    /// (skipping a bit, ending a block early, clamping values), and returns null if the data was valid or a
    /// description of the first problem.
    /// </summary>
    internal static string? DecodeMcu(BitReader r, McuLayout layout, HuffmanTable[] dc, HuffmanTable[] ac,
        Span<short> dest, int[] predictors)
    {
        dest.Clear();
        string? error = null;
        var blocks = layout.BlockArray;
        var zigzag = ZigZag.ToNatural;

        for (int b = 0; b < blocks.Length; b++)
        {
            var block = dest.Slice(b * 64, 64);

            int s = dc[b].Decode(r);
            if (s < 0)
            {
                error ??= $"invalid DC Huffman code in block {b}.";
                r.Skip(1); // make progress; the stream usually falls back into step
                s = 0;
            }
            else if (s > 11)
            {
                error ??= $"invalid DC magnitude category {s} in block {b}.";
            }

            int component = blocks[b].ComponentIndex;
            int value = predictors[component] + (s == 0 ? 0 : Extend(r.ReadBits(s), s));
            if (value is < MinDc or > MaxDc)
            {
                error ??= $"DC value {value} out of range in block {b}.";
                value = Math.Clamp(value, MinDc, MaxDc);
            }

            predictors[component] = value;
            block[0] = (short)value;

            for (int k = 1; k < 64;)
            {
                int rs = ac[b].Decode(r);
                if (rs < 0)
                {
                    error ??= $"invalid AC Huffman code in block {b}.";
                    r.Skip(1);
                    break; // treat as end of block
                }

                int run = rs >> 4, size = rs & 15;
                if (size == 0)
                {
                    if (run == 0)
                        break; // end of block
                    if (run != 15)
                    {
                        error ??= $"invalid AC symbol 0x{rs:X2} in block {b}.";
                        break;
                    }

                    k += 16; // ZRL: 16 zeros
                    if (k > 64)
                    {
                        error ??= $"AC run past end of block {b}.";
                        break;
                    }

                    continue;
                }

                if (size > 10)
                    error ??= $"invalid AC magnitude category {size} in block {b}.";

                int coefficient = Extend(r.ReadBits(size), size); // always consume the bits to stay in step
                k += run;
                if (k > 63)
                {
                    error ??= $"AC run past end of block {b}.";
                    break;
                }

                block[zigzag[k]] = (short)Math.Clamp(coefficient, -MaxAc, MaxAc);
                k++;
            }
        }

        return error;
    }

    internal static int Extend(int value, int size) =>
        value < 1 << (size - 1) ? value - (1 << size) + 1 : value;
}
