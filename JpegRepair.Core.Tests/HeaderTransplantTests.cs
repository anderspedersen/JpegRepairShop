namespace JpegRepair.Core.Tests;

public class HeaderTransplantTests
{
    /// <summary>photo.JPG with its quantization/Huffman tables and frame header zeroed: the header can't be decoded.</summary>
    private static byte[] PhotoWithBrokenHeader(out JpegFile original)
    {
        var data = TestFiles.Read(TestFiles.Photo);
        original = JpegFile.Parse(data);
        int tablesStart = original.Segments.First(s => s.Marker == Markers.DQT).Offset;
        int sosOffset = original.Segments[^1].Offset;
        Array.Clear(data, tablesStart, sosOffset - tablesStart);
        return data;
    }

    [Fact]
    public void Broken_header_is_detected()
    {
        var damaged = PhotoWithBrokenHeader(out _);

        Assert.ThrowsAny<JpegFormatException>(() => JpegDocument.Decode(damaged));
        Assert.True(HeaderTransplant.MightContainImageData(damaged));
    }

    [Fact]
    public void Tiny_or_empty_files_are_not_worth_a_transplant()
    {
        Assert.False(HeaderTransplant.MightContainImageData(new byte[500]));
        Assert.False(HeaderTransplant.MightContainImageData(new byte[100_000]));
    }

    [Fact]
    public void Header_from_the_same_camera_at_the_same_offset_restores_the_file()
    {
        var damaged = PhotoWithBrokenHeader(out var original);
        var reference = TestFiles.Read(TestFiles.Photo); // stands in for another picture from the same camera

        var candidate = HeaderTransplant.Evaluate(damaged, reference);
        Assert.Equal(HeaderMatch.SameOffset, candidate.Match);
        Assert.Equal(original.ScanDataOffset, candidate.DamagedScanOffset);

        var repaired = HeaderTransplant.Combine(reference, damaged, candidate);
        Assert.Equal(reference, repaired);
        Assert.Empty(JpegDocument.Decode(repaired).Scan.Issues);
    }

    [Fact]
    public void Same_scan_header_at_another_offset_is_only_a_weak_match()
    {
        var damaged = PhotoWithBrokenHeader(out var original);
        damaged = [.. damaged[..1000], .. damaged[1100..]]; // EXIF 100 bytes shorter: the scan starts elsewhere

        var candidate = HeaderTransplant.Evaluate(damaged, TestFiles.Read(TestFiles.Photo));

        Assert.Equal(HeaderMatch.OtherOffset, candidate.Match);
        Assert.Equal(original.ScanDataOffset - 100, candidate.DamagedScanOffset);
        var repaired = HeaderTransplant.Combine(TestFiles.Read(TestFiles.Photo), damaged, candidate);
        Assert.Empty(JpegDocument.Decode(repaired).Scan.Issues);
    }

    [Fact]
    public void Picture_with_a_different_header_size_does_not_match_at_the_same_offset()
    {
        var damaged = PhotoWithBrokenHeader(out var original);
        // Stands in for a picture from another camera: photo.JPG without its EXIF block, so its scan starts much earlier.
        var exif = original.Segments[1];
        var data = TestFiles.Read(TestFiles.Photo);
        byte[] otherPicture = [.. data[..exif.Offset], .. data[(exif.Offset + 2 + exif.Length)..]];

        var candidate = HeaderTransplant.Evaluate(damaged, otherPicture);

        // The SOS header bytes are the same in both files (they nearly always are), but not at the same place.
        Assert.NotEqual(HeaderMatch.SameOffset, candidate.Match);
    }

    [Fact]
    public void Preview_after_the_picture_is_not_taken_for_its_scan_start()
    {
        var damaged = PhotoWithBrokenHeader(out var original);
        var data = TestFiles.Read(TestFiles.Photo);
        var exifPreview = MarkerScan.Run(data).Images[0];
        damaged = [.. damaged, .. data[exifPreview.SoiOffset!.Value..(exifPreview.ScanEnd + 2)]]; // MPF-style preview after the EOI

        var candidate = HeaderTransplant.Evaluate(damaged[..1000].Concat(damaged[1100..]).ToArray(), data);

        Assert.Equal(HeaderMatch.OtherOffset, candidate.Match);
        Assert.Equal(original.ScanDataOffset - 100, candidate.DamagedScanOffset);
    }

    [Fact]
    public void Broken_reference_is_reported_as_unusable()
    {
        var damaged = PhotoWithBrokenHeader(out _);
        var brokenReference = PhotoWithBrokenHeader(out _);

        var candidate = HeaderTransplant.Evaluate(damaged, brokenReference);

        Assert.Equal(HeaderMatch.Unusable, candidate.Match);
        Assert.NotNull(candidate.Message);
    }

    [Fact]
    public void Forced_join_works_when_the_damaged_scan_header_is_gone_too()
    {
        var damaged = PhotoWithBrokenHeader(out var original);
        Array.Clear(damaged, original.Segments[^1].Offset, original.ScanDataOffset - original.Segments[^1].Offset);
        var reference = TestFiles.Read(TestFiles.Photo);

        var candidate = HeaderTransplant.Evaluate(damaged, reference);
        Assert.Equal(HeaderMatch.None, candidate.Match); // only the EXIF previews' SOS headers are left, and they don't count

        var repaired = HeaderTransplant.CombineAtSameOffset(reference, damaged, candidate);
        Assert.Equal(reference, repaired);
    }
}
