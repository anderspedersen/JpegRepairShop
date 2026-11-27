namespace JpegRepair.Core.Tests;

public class DpFixTests
{
    // photo.JPG: 100 × 150 MCUs of 16 × 8; MCU 4037 is row 40, column 37 (as in the other tests).
    private const int Place = 4037;

    private static JpegDocument LoadPhoto() => JpegDocument.Decode(TestFiles.Read(TestFiles.Photo));

    /// <summary>Applies the plan as "Fix all" does: every step, in order.</summary>
    private static void ApplyPlan(JpegDocument doc, DpPlan plan)
    {
        foreach (var step in plan.Steps)
            doc.ApplyDpStep(step);
    }

    private static void AssertInPlaceFrom(JpegDocument doc, int first)
    {
        for (int i = first; i < doc.Frame.McuCount; i++)
            Assert.Equal(i, doc[i].SourceIndex);
    }

    [Fact]
    public void Intact_photo_needs_no_edits_and_no_colour_fixes()
    {
        var doc = LoadPhoto();
        var plan = doc.PlanDpFix(colourStarts: true);

        Assert.Empty(plan.Edits);
        ApplyPlan(doc, plan);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void Lost_mcus_get_blanks_where_they_were()
    {
        var doc = LoadPhoto();
        doc.Remove(Place, 3);

        var plan = doc.PlanDpFix();

        Assert.Equal(new DpStep(Place, 3), Assert.Single(plan.Edits));
        Assert.Equal(3, plan.Inserted);
        ApplyPlan(doc, plan);
        for (int i = Place; i < Place + 3; i++)
            Assert.True(doc[i].IsBlank);
        AssertInPlaceFrom(doc, Place + 3);
    }

    [Fact]
    public void Extra_mcus_are_removed()
    {
        var doc = LoadPhoto();
        doc.InsertBlank(Place, 4);

        var plan = doc.PlanDpFix();

        Assert.Equal(new DpStep(Place, -4), Assert.Single(plan.Edits));
        Assert.Equal(4, plan.Removed);
        ApplyPlan(doc, plan);
        AssertInPlaceFrom(doc, 0);
    }

    [Fact]
    public void Lost_mcus_with_a_colour_shift_are_realigned_and_the_colours_fixed()
    {
        var doc = LoadPhoto();
        doc.Remove(Place, 3);
        doc.AdjustDc(Place, [20, 0, 0]);
        int undoSteps = 0;
        string? before = doc.UndoDescription;

        var plan = doc.PlanDpFix();

        Assert.Equal(new DpStep(Place, 3), Assert.Single(plan.Edits));
        ApplyPlan(doc, plan);
        AssertInPlaceFrom(doc, Place + 3);
        Assert.InRange(doc.GetDcChange(Place + 3, 0), -2, 2);
        Assert.InRange(doc.GetDcChange(Place + 500, 0), -2, 2);

        while (doc.UndoDescription != before)
        {
            doc.Undo();
            undoSteps++;
        }

        Assert.Equal(1, undoSteps);
        Assert.Equal(Place + 3, doc[Place].SourceIndex);
        Assert.Equal(20, doc.GetDcChange(Place, 0));
    }

    [Fact]
    public void Damage_in_two_places_is_fixed_in_one_plan()
    {
        var doc = LoadPhoto();
        doc.Remove(7020, 5);
        doc.InsertBlank(2010, 2);

        var plan = doc.PlanDpFix();

        Assert.Equal(2, plan.Edits.Count);
        Assert.Equal(new DpStep(2010, -2), plan.Edits[0]);
        Assert.Equal(5, plan.Edits[1].Shift);
        Assert.InRange(plan.Edits[1].Index, 7018, 7022);
        ApplyPlan(doc, plan);
        AssertInPlaceFrom(doc, 7030);
    }

    [Fact]
    public void Colour_shift_on_its_own_is_found_along_the_path()
    {
        var doc = LoadPhoto();
        doc.AdjustDc(Place, [32, 0, 0]);   // 20 levels (q0 = 5)
        string? before = doc.UndoDescription;

        var plan = doc.PlanDpFix(colourStarts: true);

        Assert.Empty(plan.Edits);
        var start = plan.ColourStarts.Single(s => s.Index == Place);
        Assert.InRange(start.ColourEnd, Place + 6, Place + 100);
        var deltas = doc.ApplyDpStep(start);
        Assert.InRange(deltas[0], -34, -30);
        Assert.InRange(doc.GetDcChange(Place + 500, 0), -2, 2);
        Assert.NotEqual(before, doc.UndoDescription);
    }

    [Fact]
    public void Intact_photo_has_no_colour_stretches()
    {
        Assert.Null(LoadPhoto().FindNextColourStretch(0));
    }

    [Fact]
    public void Colour_stretch_is_found_exactly_and_corrected()
    {
        var doc = LoadPhoto();
        doc.AdjustDc(Place, [40, 0, 0]);   // 25 levels (q0 = 5) on MCUs Place..Place + 9
        doc.AdjustDc(Place + 10, [-40, 0, 0]);
        string? before = doc.UndoDescription;

        var fix = doc.FindNextColourStretch(0);

        Assert.NotNull(fix);
        Assert.Equal(Place, fix.First);
        Assert.Equal(Place + 9, fix.Last);
        Assert.InRange(fix.Deltas[0], -42, -36);
        Assert.True(fix.CostAfter < fix.CostBefore / 2);
        doc.ApplyColourStretch(fix);
        for (int i = Place; i < Place + 10; i++)
            Assert.InRange(doc.GetDcChange(i, 0), -4, 4);
        Assert.Equal(0, doc.GetDcChange(Place + 10, 0));
        Assert.Null(doc.FindNextColourStretch(fix.Last + 1));

        doc.Undo();
        Assert.Equal(before, doc.UndoDescription);
        Assert.Equal(40, doc.GetDcChange(Place, 0));
    }

    [Fact]
    public void Colour_shift_on_its_own_is_found_by_the_colour_search()
    {
        var doc = LoadPhoto();
        doc.AdjustDc(Place, [20, 0, 0]);

        Assert.Empty(doc.PlanDpFix().Edits);

        var result = doc.FindColourShift();
        Assert.NotNull(result);
        Assert.Equal(0, result.Shift);
        Assert.InRange(result.Index, Place - 2, Place + 2);
        Assert.InRange(result.ColourFix![0], -22, -18);
    }
}
