namespace JpegRepair.Core.Tests;

public class DecoderTests
{
    public static TheoryData<string> Samples => TestFiles.Samples;

    [Theory]
    [MemberData(nameof(Samples))]
    public void Intact_file_decodes_without_issues(string name)
    {
        var jpeg = DecodedJpeg.Decode(TestFiles.Read(name));

        Assert.Empty(jpeg.File.Warnings);
        Assert.Empty(jpeg.Scan.Issues);
        Assert.Equal(jpeg.File.Frame.McuCount, jpeg.Scan.CountWithStatus(McuStatus.Ok));
    }

    [Fact]
    public void Headers_are_parsed()
    {
        var file = JpegFile.Parse(TestFiles.Read(TestFiles.Photo));

        Assert.Equal(1600, file.Frame.Width);
        Assert.Equal(1200, file.Frame.Height);
        Assert.Equal(16, file.Frame.McuWidth);
        Assert.Equal(8, file.Frame.McuHeight);
        Assert.Equal(100 * 150, file.Frame.McuCount);
        Assert.Equal(4, file.Frame.BlocksPerMcu);
        Assert.Equal(Markers.EOI, file.Entropy.EndMarker);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Reencoding_reproduces_the_original_scan_bytes(string name)
    {
        var data = TestFiles.Read(name);
        var file = JpegFile.Parse(data);
        var scan = ScanDecoder.Decode(file);

        var encoded = ScanEncoder.Encode(file, scan.Layout, scan.Coefficients);

        var original = data.AsSpan(file.ScanDataOffset, file.Entropy.SourceEnd - file.ScanDataOffset).ToArray();
        Assert.Equal(original, encoded);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Saving_an_unedited_file_gives_identical_bytes(string name)
    {
        var data = TestFiles.Read(name);
        Assert.Equal(data, DecodedJpeg.Decode(data).Save());
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Render_fills_the_whole_image(string name)
    {
        var jpeg = DecodedJpeg.Decode(TestFiles.Read(name));

        var pixels = jpeg.RenderBgra();

        Assert.Equal(jpeg.Width * jpeg.Height * 4, pixels.Length);
        for (int i = 3; i < pixels.Length; i += 4)
            Assert.Equal(255, pixels[i]);
        Assert.True(pixels.Distinct().Count() > 64, "Image looks flat.");
    }

    [Fact]
    public void Truncated_file_keeps_the_decoded_part_and_marks_the_rest_missing()
    {
        var data = TestFiles.Read(TestFiles.Photo);
        var file = JpegFile.Parse(data);
        int cut = file.ScanDataOffset + (data.Length - file.ScanDataOffset) / 2;

        var jpeg = DecodedJpeg.Decode(data[..cut]);

        Assert.Null(jpeg.File.Entropy.EndMarker);
        Assert.NotEmpty(jpeg.Scan.Issues);
        Assert.Equal(McuStatus.Ok, jpeg.Scan.Mcus[0].Status);
        Assert.Equal(McuStatus.Missing, jpeg.Scan.Mcus[^1].Status);
        int ok = jpeg.Scan.CountWithStatus(McuStatus.Ok);
        Assert.InRange(ok, jpeg.File.Frame.McuCount / 4, jpeg.File.Frame.McuCount - 1);

        // Still renders and saves (missing MCUs become flat grey blocks).
        Assert.Equal(jpeg.Width * jpeg.Height * 4, jpeg.RenderBgra().Length);
        var saved = jpeg.Save();
        Assert.Equal(Markers.EOI, saved[^1]);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Damaged_scan_data_never_throws(string name)
    {
        var data = TestFiles.Read(name);
        int scanStart = JpegFile.Parse(data).ScanDataOffset;
        var random = new Random(1234);

        for (int round = 0; round < 40; round++)
        {
            var damaged = (byte[])data.Clone();
            switch (round % 4)
            {
                case 0: // flip random bits
                    for (int i = 0; i < 1 + round; i++)
                        damaged[random.Next(scanStart, damaged.Length - 2)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1: // truncate
                    damaged = damaged[..random.Next(scanStart + 1, damaged.Length)];
                    break;
                case 2: // delete a chunk (the classic "shifted image")
                {
                    int at = random.Next(scanStart, damaged.Length - 100);
                    damaged = [.. damaged[..at], .. damaged[(at + random.Next(1, 100))..]];
                    break;
                }
                case 3: // overwrite a chunk with zeros
                {
                    int at = random.Next(scanStart, damaged.Length - 600);
                    Array.Clear(damaged, at, 512);
                    break;
                }
            }

            var jpeg = DecodedJpeg.Decode(damaged);
            jpeg.RenderBgra();
            Assert.NotEqual(0, jpeg.Scan.CountWithStatus(McuStatus.Ok) + jpeg.Scan.CountWithStatus(McuStatus.Corrupt));
        }
    }

    [Fact]
    public void Garbage_before_a_marker_is_skipped_with_a_warning()
    {
        var data = TestFiles.Read(TestFiles.Photo);
        // Insert three junk bytes right after SOI.
        byte[] damaged = [.. data[..2], 0x12, 0x34, 0x56, .. data[2..]];

        var jpeg = DecodedJpeg.Decode(damaged);

        Assert.Single(jpeg.File.Warnings);
        Assert.Empty(jpeg.Scan.Issues);
    }

    [Fact]
    public void Decoding_continues_through_garbage_like_the_original_tool()
    {
        // 512 bytes of foreign data in the middle of the scan. The first version of this decoder stopped at the
        // first error and left ~10,000 of 15,000 MCUs missing; it must keep going like the C++ tool did.
        var data = TestFiles.Read(TestFiles.Photo);
        int at = JpegFile.Parse(data).ScanDataOffset + 100_000;
        for (int i = 0; i < 512; i++)
            data[at + i] = (byte)((i * 37 + 11) & 0xFF);

        var jpeg = DecodedJpeg.Decode(data);

        Assert.Equal(0, jpeg.Scan.CountWithStatus(McuStatus.Missing));
    }

    [Fact]
    public void Stray_marker_inside_the_scan_does_not_cut_the_picture_off()
    {
        var data = TestFiles.Read(TestFiles.Photo);
        int at = JpegFile.Parse(data).ScanDataOffset + 100_000;
        data[at] = 0xFF;
        data[at + 1] = 0x5C; // not a marker that can appear here

        var jpeg = DecodedJpeg.Decode(data);

        Assert.Equal(Markers.EOI, jpeg.File.Entropy.EndMarker);
        Assert.Contains(jpeg.File.Warnings, w => w.Contains("Damaged data"));
        Assert.Equal(0, jpeg.Scan.CountWithStatus(McuStatus.Missing));
    }

    [Fact]
    public void Restart_markers_confine_damage_to_one_interval()
    {
        var data = TestFiles.Read(TestFiles.PhotoWithRestartMarkers);
        var file = JpegFile.Parse(data);
        Assert.Equal(100, file.RestartInterval);
        int at = file.ScanDataOffset + 150_000;
        byte[] damaged = [.. data[..at], .. data[(at + 300)..]]; // lose 300 bytes inside one interval

        var jpeg = DecodedJpeg.Decode(damaged);

        Assert.InRange(jpeg.Scan.CountWithStatus(McuStatus.Missing), 1, 100);
        Assert.Equal(McuStatus.Ok, jpeg.Scan.Mcus[^1].Status);
    }

    private static int FindMarker(byte[] data, byte marker, int from) =>
        from + data.AsSpan(from).IndexOf(new byte[] { 0xFF, marker });

    [Fact]
    public void Restart_marker_with_a_damaged_number_is_still_used()
    {
        var data = TestFiles.Read(TestFiles.PhotoWithRestartMarkers);
        int at = FindMarker(data, 0xD3, JpegFile.Parse(data).ScanDataOffset + 100_000);
        data[at + 1] = 0xD7; // RST3 -> RST7 (a single flipped bit)

        var jpeg = DecodedJpeg.Decode(data);

        // Before the fix this jumped 4 intervals at every following marker: 800 MCUs missing, 47 rows shifted.
        Assert.Equal(0, jpeg.Scan.CountWithStatus(McuStatus.Missing));
        Assert.Contains(jpeg.Scan.Issues, i => i.Message.Contains("damaged number"));
    }

    [Fact]
    public void Interval_whose_restart_marker_was_destroyed_is_still_decoded()
    {
        var data = TestFiles.Read(TestFiles.PhotoWithRestartMarkers);
        int at = FindMarker(data, 0xD5, JpegFile.Parse(data).ScanDataOffset + 100_000);
        data[at] = 0x12;
        data[at + 1] = 0x34;

        var jpeg = DecodedJpeg.Decode(data);

        Assert.Equal(0, jpeg.Scan.CountWithStatus(McuStatus.Missing));
        Assert.Contains(jpeg.Scan.Issues, i => i.Message.Contains("missing before file offset"));
    }

    [Fact]
    public void Fake_restart_marker_in_the_data_is_ignored()
    {
        var data = TestFiles.Read(TestFiles.PhotoWithRestartMarkers);
        int at = JpegFile.Parse(data).ScanDataOffset + 150_000;
        data[at] = 0xFF;
        data[at + 1] = 0xD2;

        var jpeg = DecodedJpeg.Decode(data);

        Assert.Equal(0, jpeg.Scan.CountWithStatus(McuStatus.Missing));
        Assert.Contains(jpeg.Scan.Issues, i => i.Message.StartsWith("Ignored restart marker"));
    }

    [Fact]
    public void Fake_end_of_image_in_the_data_does_not_cut_the_picture_off()
    {
        var data = TestFiles.Read(TestFiles.Photo);
        int at = JpegFile.Parse(data).ScanDataOffset + 100_000;
        data[at] = 0xFF;
        data[at + 1] = Markers.EOI;

        var jpeg = DecodedJpeg.Decode(data);

        // Before the fix two thirds of the picture (10,094 MCUs) were missing.
        Assert.Equal(0, jpeg.Scan.CountWithStatus(McuStatus.Missing));
        Assert.Contains(jpeg.File.Warnings, w => w.Contains("Damaged data"));
        Assert.Equal(data.Length - 2, jpeg.File.Entropy.SourceEnd); // ended at the real EOI
    }

    [Fact]
    public void Padding_after_the_real_end_of_image_is_left_alone()
    {
        byte[] data = [.. TestFiles.Read(TestFiles.Photo), .. new byte[4096]];

        var jpeg = DecodedJpeg.Decode(data);

        Assert.Empty(jpeg.Scan.Issues);
        Assert.Equal(data, jpeg.Save());
    }

    [Fact]
    public void Non_jpeg_input_is_rejected()
    {
        Assert.Throws<JpegFormatException>(() => JpegFile.Parse([0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0]));
    }
}
