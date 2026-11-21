namespace JpegRepair.Core.Tests;

internal static class TestFiles
{
    /// <summary>photo.JPG (from the old C++ project): 1600x1200, 4:2:2 (16x8 MCUs), EXIF with two embedded previews.</summary>
    public const string Photo = "photo.JPG";

    /// <summary>photo.JPG re-encoded with a restart marker after every MCU row (interval 100); built at test time.</summary>
    public const string PhotoWithRestartMarkers = "photo.JPG + restart markers";

    public static TheoryData<string> Samples => new() { Photo, PhotoWithRestartMarkers };

    public static byte[] Read(string name) => name == PhotoWithRestartMarkers
        ? BuildPhotoWithRestartMarkers(100)
        : File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    /// <summary>
    /// Inserts a DRI segment into photo.JPG's header and re-encodes the same coefficients with restart markers,
    /// so restart handling can be tested without a second sample file.
    /// </summary>
    private static byte[] BuildPhotoWithRestartMarkers(int interval)
    {
        var photo = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", Photo));
        var file = JpegFile.Parse(photo);
        var scan = ScanDecoder.Decode(file);

        int sos = file.Segments[^1].Offset;
        byte[] dri = [0xFF, Markers.DRI, 0x00, 0x04, (byte)(interval >> 8), (byte)interval];
        byte[] header = [.. photo.AsSpan(0, sos), .. dri, .. photo.AsSpan(sos, file.ScanDataOffset - sos)];

        var withRestarts = JpegFile.Parse([.. header, 0xFF, Markers.EOI]);
        var entropy = ScanEncoder.Encode(withRestarts, scan.Layout, scan.Coefficients);
        return [.. header, .. entropy, 0xFF, Markers.EOI];
    }
}
