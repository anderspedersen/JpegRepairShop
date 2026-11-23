namespace JpegRepair.Core.Tests;

public class StripTests
{
    // photo.JPG: 100 × 150 MCUs of 16 × 8; MCU 4037 is row 40, column 37 (as in the other tests).
    private const int Place = 4037;

    private static JpegDocument LoadPhoto() => JpegDocument.Decode(TestFiles.Read(TestFiles.Photo));

    /// <summary>
    /// The photo with MCUs 4037..4046 40 DC steps (≈ 25 levels) brighter, as after a small burst of garbage that the next
    /// one undoes. Marked as not bad, with an MCU marked bad on each side, they are a short good stripe between two bad
    /// stripes.
    /// </summary>
    private static JpegDocument PhotoWithColourStripe()
    {
        var doc = LoadPhoto();
        doc.AdjustDc(Place, [40, 0, 0]);
        doc.AdjustDc(Place + 10, [-40, 0, 0]);
        for (int i = Place; i < Place + 10; i++)
            doc.SetMark(i, McuMark.NotBad);
        doc.SetMark(Place - 1, McuMark.Bad);
        doc.SetMark(Place + 10, McuMark.Bad);
        return doc;
    }

    [Fact]
    public void Good_stripe_with_a_wrong_colour_is_corrected_as_a_whole()
    {
        var doc = PhotoWithColourStripe();
        string? before = doc.UndoDescription;

        var fix = doc.FindStripFixes().Single(f => f.First == Place);

        Assert.Equal(Place + 9, fix.Last);
        Assert.Equal(0, fix.Shift);
        Assert.InRange(fix.Deltas[0], -42, -38);
        Assert.Equal(Place, fix.ColourFirst);
        Assert.Equal(Place + 9, fix.ColourLast);
        Assert.True(fix.CostAfter < fix.CostBefore / 2);

        doc.ApplyStripFix(fix);
        for (int i = Place; i < Place + 10; i++)
            Assert.InRange(doc.GetDcChange(i, 0), -2, 2);
        Assert.Equal(0, doc.GetDcChange(Place + 10, 0));

        doc.Undo();
        Assert.Equal(before, doc.UndoDescription);
        Assert.Equal(40, doc.GetDcChange(Place, 0));
    }

    [Fact]
    public void Bad_stripe_with_a_wrong_colour_is_corrected_as_a_whole()
    {
        var doc = LoadPhoto();

        // The same brighter stretch without marks: the sensitive detection flags all of it, so it is one bad stripe,
        // which is really a good stretch with a wrong colour.
        doc.AdjustDc(Place, [40, 0, 0]);
        doc.AdjustDc(Place + 10, [-40, 0, 0]);

        var fix = doc.FindStripFix();

        Assert.NotNull(fix);
        Assert.True(fix.IsBadStripe);
        Assert.Equal(0, fix.Shift);
        Assert.InRange(fix.Deltas[0], -42, -38);
        Assert.Equal(Place, fix.ColourFirst);
        Assert.Equal(Place + 9, fix.ColourLast);

        doc.ApplyStripFix(fix);
        for (int i = Place; i < Place + 10; i++)
            Assert.InRange(doc.GetDcChange(i, 0), -2, 2);
    }

    [Fact]
    public void Intact_photo_needs_no_stripe_fixes_of_note()
    {
        var doc = LoadPhoto();

        var fix = doc.FindStripFix();

        Assert.True(fix == null || Math.Abs(fix.Deltas[0]) <= 3, fix == null ? "" : doc.DescribeStripFix(fix));
    }

    [Fact]
    public void Move_inserts_and_removes_at_the_given_points_only()
    {
        var doc = LoadPhoto();

        // MCU 4037 lost: 4038..4047 sit one place too far left, then a blank puts the rest back in place.
        doc.Remove(Place);
        doc.InsertBlank(Place + 10);
        Assert.Equal(Place + 1, doc[Place].SourceIndex);

        // Move 4037..4046 right by 1: a blank inserted before 4037, the blank at 4047 removed (at 4048 after the insert).
        var region = Enumerable.Range(Place, 11).Select(i => doc[i]).ToArray();
        var fix = new StripFix(Place, Place + 9, false, Place, region, shift: 1, insertAt: Place, removeAt: Place + 11,
            colourFirst: -1, colourLast: -1, deltas: [0, 0, 0], costBefore: 1, costAfter: 0);
        Assert.True(doc.CanApplyStripFix(fix));
        doc.ApplyStripFix(fix);

        Assert.True(doc[Place].IsBlank);
        for (int i = Place + 1; i <= Place + 12; i++)
            Assert.Equal(i, doc[i].SourceIndex);
        Assert.Equal("Fine-tune MCUs 4037–4046: moved right by 1 (a blank inserted before MCU 4037, MCU 4047 removed)",
            doc.UndoDescription);

        doc.Undo();
        Assert.Equal(Place + 1, doc[Place].SourceIndex);
        Assert.True(doc[Place + 10].IsBlank);
    }

    [Fact]
    public void Fix_is_rejected_when_its_mcus_have_moved()
    {
        var doc = PhotoWithColourStripe();
        var fix = doc.FindStripFixes().Single(f => f.First == Place);

        doc.Remove(Place - 50);

        Assert.False(doc.CanApplyStripFix(fix));
        Assert.Throws<InvalidOperationException>(() => doc.ApplyStripFix(fix));
    }
}
