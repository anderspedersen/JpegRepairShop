namespace JpegRepair.Core.Tests;

public class MarkerScanTests
{
    // photo.JPG: APP1 at offset 2 holds a 640x480 preview (SOI at 2868) and a 160x120 thumbnail (SOI at 58376).
    private const int Preview640Soi = 2868;

    [Fact]
    public void Finds_the_main_picture_and_the_exif_previews()
    {
        var data = TestFiles.Read(TestFiles.Photo);
        var file = JpegFile.Parse(data);

        var scan = MarkerScan.Run(data);

        Assert.Equal(3, scan.Images.Count);
        Assert.Equal(new[] { 640, 160, 1600 }, scan.Images.Select(i => i.Width));
        Assert.True(scan.Images[0].InsideExif);
        Assert.True(scan.Images[1].InsideExif);
        Assert.NotNull(scan.Main);
        Assert.False(scan.Main.InsideExif);
        Assert.Equal(file.ScanDataOffset, scan.Main.ScanDataOffset);
        Assert.Equal(file.Entropy.SourceEnd, scan.Main.ScanEnd);
        Assert.Equal(file.Segments.Where(s => s.Marker is Markers.DQT or Markers.DHT), scan.Main.Tables);
    }

    [Fact]
    public void Preview_appended_after_the_picture_is_not_the_main_picture()
    {
        var data = TestFiles.Read(TestFiles.Photo);
        byte[] withPreview = [.. data, .. data[Preview640Soi..(MarkerScan.Run(data).Images[0].ScanEnd + 2)]];

        var scan = MarkerScan.Run(withPreview);

        Assert.Equal(4, scan.Images.Count);
        Assert.Equal(1600, scan.Main!.Width);
        Assert.False(scan.Images[^1].InsideExif);
        Assert.Equal(data.Length, scan.Images[^1].SoiOffset);
    }
/*
    [Fact]
    public void Exif_length_too_short_no_longer_leads_to_the_preview()
    {
        var original = TestFiles.Read(TestFiles.Photo);
        var data = original.ToArray();
        // The APP1 length now ends the segment just before the 640x480 preview's tables: the chain skips to its DHT.
        data[4] = (byte)(2896 >> 8);
        data[5] = (byte)2896;

        var jpeg = DecodedJpeg.Decode(data);

        Assert.Equal(1600, jpeg.File.Frame.Width);
        Assert.Equal(JpegFile.Parse(original).ScanDataOffset, jpeg.File.ScanDataOffset);
        Assert.Contains(jpeg.File.Warnings, w => w.Contains("preview"));
        Assert.Empty(jpeg.Scan.Issues);
        Assert.Empty(DecodedJpeg.Decode(jpeg.Save()).File.Warnings); // the rebuilt header is a clean header
    }

    [Fact]
    public void Exif_length_pointing_into_the_exif_data_is_recovered()
    {
        var data = TestFiles.Read(TestFiles.Photo);
        data[4] = (byte)(40000 >> 8);
        data[5] = (byte)40000;

        var jpeg = DecodedJpeg.Decode(data);

        Assert.Equal(1600, jpeg.File.Frame.Width);
        Assert.Empty(jpeg.Scan.Issues);
    }
*/
    [Fact]
    public void Destroyed_start_of_file_is_recovered()
    {
        var data = TestFiles.Read(TestFiles.Photo);
        Array.Clear(data, 0, 4096); // SOI and the start of the EXIF data

        var jpeg = DecodedJpeg.Decode(data);

        Assert.Equal(1600, jpeg.File.Frame.Width);
        Assert.Contains(jpeg.File.Warnings, w => w.Contains("searching for markers"));
        Assert.Empty(jpeg.Scan.Issues);
        Assert.Equal(0xFF, jpeg.File.Header.Span[0]);
        Assert.Equal(Markers.SOI, jpeg.File.Header.Span[1]);
    }

    [Fact]
    public void Picture_after_foreign_data_keeps_its_own_header()
    {
        var original = TestFiles.Read(TestFiles.Photo);
        byte[] data = [.. Enumerable.Repeat((byte)0x11, 10_000), .. original];

        var jpeg = DecodedJpeg.Decode(data);

        Assert.Equal(10_000 + JpegFile.Parse(original).ScanDataOffset, jpeg.File.ScanDataOffset);
        Assert.Contains(jpeg.File.Warnings, w => w.Contains("starts at offset 10000"));
        Assert.Equal(original, jpeg.Save());
    }

    [Fact]
    public void Missing_tables_are_still_an_error()
    {
        var data = TestFiles.Read(TestFiles.Photo);
        var file = JpegFile.Parse(data);
        var dqt = file.Segments.First(s => s.Marker == Markers.DQT);
        Array.Clear(data, dqt.Offset, 2 + dqt.Length);
        Array.Clear(data, 0, 2); // force the marker search as well

        Assert.ThrowsAny<JpegFormatException>(() => DecodedJpeg.Decode(data));
    }

    [Fact]
    public void Only_exif_previews_left_is_an_error()
    {
        var data = TestFiles.Read(TestFiles.Photo);
        var file = JpegFile.Parse(data);
        int tablesStart = file.Segments.First(s => s.Marker == Markers.DQT).Offset;
        Array.Clear(data, tablesStart, file.ScanDataOffset - tablesStart); // the main picture's whole header

        var scan = MarkerScan.Run(data);

        Assert.Null(scan.Main);
        Assert.ThrowsAny<JpegFormatException>(() => JpegFile.Parse(data));
    }
}
