namespace JpegRepair.Core.Tests;

public class SectionTests
{
    // photo.JPG: 100 × 150 MCUs of 16 × 8.
    private const int Place = 4037;

    private static JpegDocument LoadPhoto() => JpegDocument.Decode(TestFiles.Read(TestFiles.Photo));

    /// <summary>The section an intact photo has around <paramref name="index"/>: bounded only by its own flagged MCUs.</summary>
    private static McuSection ExpectedIntact(JpegDocument doc, int index)
    {
        var bad = doc.FindBadMcus().BadIndices().ToList();
        int first = bad.Where(i => i < index).Select(i => i + 1).DefaultIfEmpty(0).Max();
        int last = bad.Where(i => i > index).Select(i => i - 1).DefaultIfEmpty(doc.Frame.McuCount - 1).Min();
        return new McuSection(first, last);
    }

    [Fact]
    public void Section_of_an_intact_photo_reaches_its_flagged_mcus_or_the_ends()
    {
        var doc = LoadPhoto();

        var section = doc.FindSection(500);

        Assert.NotNull(section);
        Assert.Equal(ExpectedIntact(doc, 500), section.Value);
        Assert.True(section.Value.Contains(500));
    }

    [Fact]
    public void Blank_mcu_ends_a_section_and_has_none_itself()
    {
        var doc = LoadPhoto();
        doc.InsertBlank(Place);

        Assert.False(doc.IsGood(Place));
        Assert.Null(doc.FindSection(Place));
        Assert.Equal(Place - 1, doc.FindSection(Place - 1)!.Value.Last);
        Assert.True(doc.FindSection(Place + 1) is not { } after || after.First == Place + 1);
    }

    [Fact]
    public void Flagged_mcu_ends_a_section()
    {
        var doc = LoadPhoto();

        // Only MCU 4037 gets much brighter: flagged as bad (see BadMcuTests), so it splits the section.
        doc.AdjustDc(Place, [40, 0, 0]);
        doc.AdjustDc(Place + 1, [-40, 0, 0]);

        Assert.True(doc.FindBadMcus().IsBad(Place));
        Assert.Null(doc.FindSection(Place));
        Assert.Equal(Place - 1, doc.FindSection(Place - 10)!.Value.Last);
        Assert.Equal(Place + 1, doc.FindSection(Place + 10)!.Value.First);
    }

    [Fact]
    public void Section_follows_undo()
    {
        var doc = LoadPhoto();
        var intact = doc.FindSection(Place - 10);

        doc.InsertBlank(Place);
        Assert.NotEqual(intact, doc.FindSection(Place - 10));

        doc.Undo();
        Assert.Equal(intact, doc.FindSection(Place - 10));
    }

    [Fact]
    public void Section_colours_are_matched_to_the_mcus_before_it()
    {
        var doc = LoadPhoto();

        // Colours shift from 4037 on; the MCU where the shift starts is flagged, so a new section starts right after it.
        doc.AdjustDc(Place, [20, -6, 9]);
        var estimate = doc.EstimateSectionColourShift(Place + 150);

        Assert.NotNull(estimate);
        var (section, deltas) = estimate.Value;
        Assert.InRange(section.First, Place - 1, Place + 2);
        Assert.InRange(deltas[0], -24, -15);
        Assert.InRange(deltas[1], 4, 8);
        Assert.InRange(deltas[2], -11, -7);
    }

    [Fact]
    public void Section_colour_fix_is_one_undo_step_from_the_section_start()
    {
        var doc = LoadPhoto();
        doc.AdjustDc(Place, [20, -6, 9]);

        var result = doc.FixSectionColours(Place + 150);

        Assert.NotNull(result);
        int luma = result.Value.Deltas[0];
        Assert.NotEqual(0, luma);
        Assert.Equal(20 + luma, doc.GetDcChange(Place + 150, 0));   // inside the section
        Assert.Equal(0, doc.GetDcChange(Place - 2, 0));             // before it: untouched
        doc.Undo();
        Assert.Equal(20, doc.GetDcChange(Place + 150, 0));
    }

    [Fact]
    public void A_section_starting_at_the_first_mcu_has_nothing_to_compare_with()
    {
        Assert.Null(LoadPhoto().EstimateSectionColourShift(0));
    }
}
