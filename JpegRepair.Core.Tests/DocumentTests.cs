namespace JpegRepair.Core.Tests;

public class DocumentTests
{
    private static JpegDocument LoadPhoto(out byte[] original)
    {
        original = TestFiles.Read("photo.JPG");
        return JpegDocument.Decode(original);
    }

    [Fact]
    public void Unedited_document_saves_identical_bytes()
    {
        var doc = LoadPhoto(out var original);

        Assert.False(doc.IsModified);
        Assert.Equal(original, doc.Save());
    }

    [Fact]
    public void Insert_then_undo_restores_the_file()
    {
        var doc = LoadPhoto(out var original);

        doc.InsertBlank(500);
        Assert.True(doc.IsModified);
        Assert.Equal(McuStatus.Blank, doc.GetStatus(500));
        Assert.Equal(1, doc.OverflowCount);              // the last MCU was pushed out of the frame
        Assert.NotEqual(original, doc.Save());

        doc.Undo();
        Assert.Equal(0, doc.OverflowCount);
        Assert.Equal(original, doc.Save());
    }

    [Fact]
    public void Remove_then_undo_restores_the_file()
    {
        var doc = LoadPhoto(out var original);
        int count = doc.Frame.McuCount;

        doc.Remove(0);
        Assert.Equal(count - 1, doc.Count);
        Assert.Equal(McuStatus.Missing, doc.GetStatus(count - 1)); // nothing left to pull in
        Assert.Equal(doc.Width * doc.Height * 4, doc.RenderBgra().Length);

        doc.Undo();
        Assert.Equal(original, doc.Save());
    }

    [Fact]
    public void Insert_and_remove_at_the_same_place_cancel_out()
    {
        var doc = LoadPhoto(out var original);

        doc.InsertBlank(1234);
        doc.Remove(1234);

        Assert.Equal(original, doc.Save());
    }

    [Fact]
    public void Redo_reapplies_an_undone_edit()
    {
        var doc = LoadPhoto(out _);
        doc.AdjustDc(100, [5, 0, 0]);
        var edited = doc.Save();

        doc.Undo();
        Assert.True(doc.CanRedo);
        doc.Redo();

        Assert.Equal(edited, doc.Save());
        Assert.False(doc.CanRedo);
    }

    [Fact]
    public void Adjust_dc_changes_only_mcus_from_the_index()
    {
        var doc = LoadPhoto(out _);
        short before99 = doc.GetDc(99, 0), before100 = doc.GetDc(100, 0), cb100 = doc.GetDc(100, 2);

        doc.AdjustDc(100, [7, 0, 0]);

        Assert.Equal(before99, doc.GetDc(99, 0));
        Assert.Equal(before100 + 7, doc.GetDc(100, 0));
        Assert.Equal(cb100, doc.GetDc(100, 2)); // block 2 is Cb in a 4:2:2 MCU (Y Y Cb Cr)
    }

    [Fact]
    public void Auto_fix_undoes_an_artificial_colour_shift()
    {
        var doc = LoadPhoto(out var original);
        int index = 3 * doc.Frame.McusPerRow + doc.Frame.McusPerRow / 3; // row 3, a third of the way across

        Assert.Equal(new[] { 0, 0, 0 }, doc.EstimateDcShift(index));

        doc.AdjustDc(index, [20, -6, 9]);
        var applied = doc.AutoFixColors(index);

        Assert.Equal(new[] { -20, 6, -9 }, applied);
        Assert.Equal(original, doc.Save());
    }

    [Fact]
    public void Repeated_nudges_at_one_mcu_become_one_undo_step()
    {
        var doc = LoadPhoto(out var original);
        short before = doc.GetDc(300, 0);

        for (int i = 0; i < 5; i++)
            doc.AdjustDc(300, [1, 0, 0], mergeWithPrevious: true);
        doc.AdjustDc(300, [0, 0, -2], mergeWithPrevious: true);

        Assert.Equal(before + 5, doc.GetDc(300, 0));
        Assert.Equal(5, doc.GetDcChange(300, 0));
        Assert.Contains("+5, 0, -2", doc.UndoDescription);

        doc.Undo();
        Assert.False(doc.CanUndo);
        Assert.Equal(original, doc.Save());
    }

    [Fact]
    public void Nudges_at_a_different_mcu_or_after_saving_start_a_new_undo_step()
    {
        var doc = LoadPhoto(out _);

        doc.AdjustDc(300, [1, 0, 0], mergeWithPrevious: true);
        doc.AdjustDc(301, [1, 0, 0], mergeWithPrevious: true);  // different MCU
        doc.Save();
        doc.AdjustDc(301, [1, 0, 0], mergeWithPrevious: true);  // after saving

        doc.Undo();
        doc.Undo();
        Assert.True(doc.CanUndo);
        doc.Undo();
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void Huge_dc_adjustment_still_saves()
    {
        var doc = LoadPhoto(out _);
        doc.AdjustDc(0, [5000, -5000, 0]);

        var saved = doc.Save(); // DC is clamped to the baseline range instead of failing

        Assert.Empty(JpegDocument.Decode(saved).Scan.Issues);
    }

    private static int AlignmentTestIndex(JpegDocument doc) => 40 * doc.Frame.McusPerRow + 37; // row 40, column 37

    [Fact]
    public void Alignment_finds_how_many_mcus_are_missing()
    {
        var doc = LoadPhoto(out _);
        int index = AlignmentTestIndex(doc);
        doc.Remove(index, 7); // as if the data of 7 MCUs was lost

        var estimate = doc.EstimateAlignment(index);

        Assert.NotNull(estimate);
        Assert.Equal(7, estimate.Shift); // insert 7
        Assert.True(estimate.Fit > estimate.FitWithoutShift + 0.2, $"fit {estimate.Fit:P0} vs {estimate.FitWithoutShift:P0}");
    }

    [Fact]
    public void Alignment_finds_how_many_mcus_are_extra()
    {
        var doc = LoadPhoto(out _);
        int index = AlignmentTestIndex(doc);
        doc.InsertBlank(index, 5);

        var estimate = doc.EstimateAlignment(index);

        Assert.NotNull(estimate);
        Assert.Equal(-5, estimate.Shift); // remove 5
    }

    [Fact]
    public void Intact_row_needs_no_alignment()
    {
        var doc = LoadPhoto(out _);

        var estimate = doc.EstimateAlignment(AlignmentTestIndex(doc));

        Assert.NotNull(estimate);
        Assert.Equal(0, estimate.Shift);
    }

    [Fact]
    public void First_row_cannot_be_aligned()
    {
        var doc = LoadPhoto(out _);
        Assert.Null(doc.EstimateAlignment(10));
    }

    [Fact]
    public void Auto_align_fixes_the_shift_in_one_undo_step()
    {
        var doc = LoadPhoto(out var original);
        int index = AlignmentTestIndex(doc);
        doc.Remove(index, 7);
        var damaged = doc.Save();

        var estimate = doc.AutoAlign(index);

        Assert.Equal(7, estimate?.Shift);
        Assert.Equal(McuStatus.Blank, doc.GetStatus(index + 6));
        Assert.Equal(index + 7, doc[index + 7].SourceIndex); // everything after the gap is back in place
        doc.Undo();
        Assert.Equal(damaged, doc.Save());
        doc.Undo();
        Assert.Equal(original, doc.Save());
    }

    [Fact]
    public void Intact_picture_has_no_colour_shift()
    {
        var doc = LoadPhoto(out _);
        Assert.Null(doc.FindColourShift());
    }

    [Fact]
    public void Align_and_fix_colors_also_fixes_the_colour_shift()
    {
        var doc = LoadPhoto(out _);
        int index = AlignmentTestIndex(doc);
        doc.Remove(index, 7);
        doc.AdjustDc(index, [20, -6, 9]); // the broken DC prediction after lost data
        var damaged = doc.Save();

        var colourFix = doc.AlignAndFixColors(index, 7);

        Assert.Equal(new[] { -20, 6, -9 }, colourFix);
        Assert.Equal(index + 7, doc[index + 7].SourceIndex);
        for (int b = 0; b < doc.Layout.BlocksPerMcu; b++) // DC is back to the decoded values
            Assert.Equal(0, doc.GetDcChange(index + 7, b));
        doc.Undo(); // one step
        Assert.Equal(damaged, doc.Save());
    }

    [Fact]
    public void Colour_shift_without_misalignment_is_found_and_fixed()
    {
        var doc = LoadPhoto(out var original);
        int index = AlignmentTestIndex(doc);
        doc.AdjustDc(index, [20, -6, 9]);
        var damaged = doc.Save();

        var result = doc.FindColourShift();

        Assert.NotNull(result);
        Assert.Equal(index, result.Index);
        Assert.Equal(0, result.Shift);
        Assert.Equal(new[] { -20, 6, -9 }, result.ColourFix);
        Assert.Equal(new[] { -20, 6, -9 }, doc.AlignAndFixColors(result.Index, 0));
        Assert.Equal(original, doc.Save());
        Assert.Null(doc.FindColourShift()); // nothing left to fix
        doc.Undo(); // one step
        Assert.Equal(damaged, doc.Save());
    }

    [Fact]
    public void Colour_only_shift_in_chroma_is_found()
    {
        var doc = LoadPhoto(out _);
        int index = AlignmentTestIndex(doc);
        doc.AdjustDc(index, [0, 0, 12]);

        var result = doc.FindColourShift();

        Assert.NotNull(result);
        Assert.Equal(index, result.Index);
        Assert.Equal(new[] { 0, 0, -12 }, result.ColourFix);
    }

    [Fact]
    public void Small_colour_differences_are_left_alone()
    {
        var doc = LoadPhoto(out _);
        doc.AdjustDc(AlignmentTestIndex(doc), [5, 0, 0]); // 5 DC steps = about 3 levels in photo.JPG

        Assert.Null(doc.FindColourShift());
    }

    [Fact]
    public void Saving_clears_the_modified_flag()
    {
        var doc = LoadPhoto(out _);
        doc.Remove(10);
        Assert.True(doc.IsModified);

        doc.Save();
        Assert.False(doc.IsModified);

        doc.Undo();
        Assert.True(doc.IsModified);
    }

    [Fact]
    public void Changed_event_reports_the_first_affected_mcu()
    {
        var doc = LoadPhoto(out _);
        var reported = new List<int>();
        doc.Changed += (_, first) => reported.Add(first);

        doc.InsertBlank(42);
        doc.Undo();

        Assert.Equal(new[] { 42, 42 }, reported);
    }

    [Fact]
    public void Inserted_garbage_shows_up_as_overflow_mcus()
    {
        var data = TestFiles.Read("photo.JPG");
        int at = JpegFile.Parse(data).ScanDataOffset + 100_000;
        var junk = new byte[400];
        new Random(7).NextBytes(junk);
        for (int i = 0; i < junk.Length; i++)
            if (junk[i] == 0xFF) junk[i] = 0x7F; // no accidental markers
        byte[] damaged = [.. data[..at], .. junk, .. data[at..]];

        var doc = JpegDocument.Decode(damaged);

        // Garbage either breaks decoding (MCU marked corrupt) or decodes as extra MCUs that push real ones
        // past the end of the frame. Either way: no exception, and overflow is reported when present.
        Assert.NotEmpty(doc.Scan.Issues);
        if (doc.OverflowCount > 0)
            Assert.Contains(doc.Scan.Issues, i => i.Message.Contains("extra MCU"));
        Assert.Equal(doc.Width * doc.Height * 4, doc.RenderBgra().Length);
    }
}
