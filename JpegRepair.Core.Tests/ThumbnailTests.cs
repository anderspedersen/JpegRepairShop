namespace JpegRepair.Core.Tests;

public class ThumbnailTests
{
    // photo.JPG: 100 × 150 MCUs of 16 × 8, previews of 640 × 480 and 160 × 120; MCU 4037 is row 40, column 37.
    private const int Place = 4037;

    private static JpegDocument LoadPhoto() => JpegDocument.Decode(TestFiles.Read(TestFiles.Photo));

    /// <summary>DC steps of component 0 that make <paramref name="levels"/> levels.</summary>
    private static int Steps(JpegDocument doc, float levels)
    {
        int q = doc.File.GetQuantizationTable(doc.Frame.Components[0].QuantTableId)!.Values[0];
        return Math.Max(1, (int)Math.Ceiling(levels * 8 / q));
    }

    [Fact]
    public void The_largest_preview_registers_without_letterbox()
    {
        var preview = LoadPhoto().FindThumbnail();

        Assert.NotNull(preview);
        Assert.Equal(640, preview.Width);
        Assert.Equal(480, preview.Height);
        Assert.True(preview.Correlation > 0.95f, $"correlation {preview.Correlation}");
        Assert.InRange(preview.Scale, 0.39, 0.41);
        Assert.InRange(preview.OffsetX, -1.0, 1.0);
        Assert.InRange(preview.OffsetY, -1.0, 1.0);
    }

    [Fact]
    public void Intact_photo_keeps_its_colours()
    {
        var doc = LoadPhoto();

        Assert.Equal((0, 0), doc.ApplyThumbnailColours(doc.FindThumbnail()!));
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void A_shifted_run_after_blanks_gets_the_preview_colours()
    {
        var doc = LoadPhoto();
        var preview = doc.FindThumbnail()!;
        doc.Remove(Place, 3);
        doc.InsertBlank(Place, 3);
        doc.AdjustDc(Place + 3, [Steps(doc, 30), 0, 0]);

        Assert.Equal((1, 0), doc.ApplyThumbnailColours(preview));
        int tolerance = Steps(doc, 2);
        Assert.Equal(0, doc.GetDcChange(Place - 200, 0));
        Assert.InRange(doc.GetDcChange(Place + 200, 0), -tolerance, tolerance);
        Assert.InRange(doc.GetDcChange(Place + 5000, 0), -tolerance, tolerance);
    }

    [Fact]
    public void A_colour_step_inside_a_run_is_left_alone()
    {
        var doc = LoadPhoto();
        var preview = doc.FindThumbnail()!;
        int steps = Steps(doc, 30);
        doc.AdjustDc(Place, [steps, 0, 0]);

        Assert.Equal((0, 1), doc.ApplyThumbnailColours(preview));
        Assert.Equal(steps, doc.GetDcChange(Place + 200, 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Lost_mcus_get_blanks_with_and_without_the_preview(bool useThumbnail)
    {
        var doc = LoadPhoto();
        doc.Remove(Place, 3);

        var plan = doc.PlanDpFix(useThumbnail: useThumbnail);

        Assert.Equal(useThumbnail, plan.Thumbnail != null);
        Assert.Equal(new DpStep(Place, 3), Assert.Single(plan.Edits));
    }

    [Fact]
    public void An_intact_photo_with_a_preview_needs_no_edits()
    {
        var plan = LoadPhoto().PlanDpFix(useThumbnail: true);

        Assert.NotNull(plan.Thumbnail);
        Assert.Empty(plan.Edits);
    }
}
