namespace JpegRepair.Core;

public enum HeaderMatch
{
    /// <summary>The reference file is not a usable JPEG itself.</summary>
    Unusable,

    /// <summary>The damaged file has the reference's start-of-scan header at exactly the same offset. Strong match.</summary>
    SameOffset,

    /// <summary>
    /// The damaged file has the same start-of-scan header, but at another offset (e.g. because the EXIF data has a
    /// different size). Weak match: almost all baseline colour JPEGs share the same start-of-scan header bytes.
    /// </summary>
    OtherOffset,

    /// <summary>The start-of-scan header was not found in the damaged file.</summary>
    None,
}

/// <param name="Match">How well the reference fits.</param>
/// <param name="ReferenceScanOffset">Where the image data starts in the reference (just after its SOS header).</param>
/// <param name="DamagedScanOffset">Where the image data starts in the damaged file, or -1 if not found.</param>
/// <param name="Message">Why a reference was unusable, otherwise null.</param>
public sealed record HeaderCandidate(HeaderMatch Match, int ReferenceScanOffset, int DamagedScanOffset, string? Message = null);

/// <summary>
/// Repairs a file whose header (everything before the image data) is damaged by borrowing the header of another
/// picture, ideally from the same camera with the same settings. The image data of the damaged file is kept;
/// only the bytes before it are replaced.
/// </summary>
public static class HeaderTransplant
{
    /// <summary>Files at least this large can contain image data after a typical header.</summary>
    public const int MinimumSize = 2048;

    /// <summary>True if the file is big enough and not empty (all zeros), so it can hold image data worth saving.</summary>
    public static bool MightContainImageData(byte[] damaged) =>
        damaged.Length >= MinimumSize && damaged.AsSpan(MinimumSize / 2).IndexOfAnyExcept((byte)0) >= 0;

    /// <summary>
    /// Checks a reference picture against the damaged file. The reference header is parsed up to its start of scan
    /// (SOS); the damaged file must have the identical SOS header at the same offset for a strong match.
    /// </summary>
    public static HeaderCandidate Evaluate(byte[] damaged, byte[] reference)
    {
        JpegFile file;
        try
        {
            file = JpegFile.Parse(reference);
            _ = new McuLayout(file.Frame, file.Scan).ResolveHuffmanTables(file);
        }
        catch (JpegFormatException ex)
        {
            return new HeaderCandidate(HeaderMatch.Unusable, -1, -1, ex.Message);
        }

        int sosOffset = file.Segments[^1].Offset;   // the last parsed segment is the SOS
        int scanOffset = file.ScanDataOffset;
        var sos = reference.AsSpan(sosOffset, scanOffset - sosOffset);

        if (damaged.Length > scanOffset && damaged.AsSpan(sosOffset, sos.Length).SequenceEqual(sos))
            return new HeaderCandidate(HeaderMatch.SameOffset, scanOffset, scanOffset);

        // Embedded EXIF previews have SOS headers too; the main image's SOS is the last one (scan data never contains 0xFF 0xDA).
        int found = damaged.AsSpan().LastIndexOf(sos);
        return found >= 0
            ? new HeaderCandidate(HeaderMatch.OtherOffset, scanOffset, found + sos.Length)
            : new HeaderCandidate(HeaderMatch.None, scanOffset, -1);
    }

    /// <summary>The reference's bytes up to its image data, followed by the damaged file's bytes from its image data on.</summary>
    public static byte[] Combine(byte[] reference, byte[] damaged, HeaderCandidate candidate)
    {
        if (candidate.ReferenceScanOffset < 0 || candidate.DamagedScanOffset < 0)
            throw new ArgumentException("The candidate has no start of scan to join at.", nameof(candidate));

        return [.. reference.AsSpan(0, candidate.ReferenceScanOffset), .. damaged.AsSpan(candidate.DamagedScanOffset)];
    }

    /// <summary>
    /// Joins at the reference's scan offset without any check, for when the damaged file's own SOS header is
    /// destroyed too. Only sensible with a reference from the same camera and settings.
    /// </summary>
    public static byte[] CombineAtSameOffset(byte[] reference, byte[] damaged, HeaderCandidate candidate)
    {
        if (candidate.ReferenceScanOffset < 0 || candidate.ReferenceScanOffset >= damaged.Length)
            throw new ArgumentException("The damaged file is shorter than the reference header.", nameof(candidate));
        return Combine(reference, damaged, candidate with { DamagedScanOffset = candidate.ReferenceScanOffset });
    }
}
