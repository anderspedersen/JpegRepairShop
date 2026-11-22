namespace JpegRepair.Core.Tests;

public class BadMcuTests
{
    // photo.JPG: 100 × 150 MCUs of 16 × 8; MCU 4037 is row 40, column 37 (as in the alignment tests).
    private const int Place = 4037;
    private const int PerRow = 100;

    private static JpegDocument LoadPhoto() => JpegDocument.Decode(TestFiles.Read(TestFiles.Photo));

    /// <summary>MCUs flagged in the intact photo (a sharp detail may be flagged).</summary>
    private static HashSet<int> IntactFlags() => LoadPhoto().FindBadMcus().BadIndices().ToHashSet();

    [Fact]
    public void Intact_photo_has_almost_no_bad_mcus()
    {
        Assert.InRange(LoadPhoto().FindBadMcus().Count, 0, 2);
    }

    [Fact]
    public void Mcu_that_differs_from_all_neighbours_is_found()
    {
        var intact = IntactFlags();
        var doc = LoadPhoto();

        // Only MCU 4037 gets much brighter (+40 DC steps = +25 levels): all 4 edges become rugged.
        doc.AdjustDc(Place, [40, 0, 0]);
        doc.AdjustDc(Place + 1, [-40, 0, 0]);
        var map = doc.FindBadMcus();

        Assert.True(map.IsBad(Place));
        Assert.Equal(BadMcuMap.Top | BadMcuMap.Right | BadMcuMap.Bottom | BadMcuMap.Left, map.RuggedEdges(Place));
        Assert.Equal(new[] { Place }, map.BadIndices().Where(i => !intact.Contains(i)));
    }

    [Fact]
    public void Colour_drift_marks_only_the_row_where_it_starts()
    {
        var intact = IntactFlags();
        var doc = LoadPhoto();

        // Everything from 4037 on is brighter: each MCU of row 40 from there has one rugged edge (above), only 4037
        // also has one to its left.
        doc.AdjustDc(Place, [40, 0, 0]);
        var added = doc.FindBadMcus().BadIndices().Where(i => !intact.Contains(i)).ToList();

        Assert.Contains(Place, added);
        Assert.True(added.Count <= 4, $"flagged: {string.Join(", ", added)}");
        Assert.All(added, i => Assert.InRange(i, Place - 1, 41 * PerRow - 1));
    }

    [Fact]
    public void Detection_follows_edits_and_undo()
    {
        var intact = IntactFlags();
        var doc = LoadPhoto();
        doc.AdjustDc(Place, [40, 0, 0]);
        doc.AdjustDc(Place + 1, [-40, 0, 0]);
        Assert.True(doc.FindBadMcus().IsBad(Place));

        doc.Undo();
        doc.Undo();

        Assert.True(intact.SetEquals(doc.FindBadMcus().BadIndices()));
    }

    [Fact]
    public void Blank_mcus_are_not_reported()
    {
        var intact = IntactFlags();
        var doc = LoadPhoto();

        // A blank MCU is marked in blue already; it and the edges to it don't count. Inserting it shifts the rest of
        // the picture right by one: rows still fit the rows above them, except at the line starts, where the last
        // MCU of a row now sits next to the first of the next. So only line starts may be added, and the intact
        // picture's own flags move one step right.
        doc.InsertBlank(Place);
        var map = doc.FindBadMcus();

        Assert.False(map.IsBad(Place));
        Assert.All(map.BadIndices(), i => Assert.True(i % PerRow == 0 || intact.Contains(i - 1), $"MCU {i}"));
    }

    [Fact]
    public void Colour_fix_ignores_garbage_in_the_row_above()
    {
        var doc = LoadPhoto();

        // In the 100 MCUs above 4037..4136 (3937..4036), two of every three get much brighter, MCU by MCU, like
        // garbage that decoded without errors: rugged above/below and left/right. Then everything from 4037 on gets
        // brighter by 20 DC steps.
        for (int i = Place - PerRow; i < Place; i++)
        {
            if ((i - (Place - PerRow)) % 3 != 2)
            {
                doc.AdjustDc(i, [60, 0, 0]);
                doc.AdjustDc(i + 1, [-60, 0, 0]);
            }
        }

        doc.AdjustDc(Place, [20, 0, 0]);
        var deltas = doc.EstimateDcShift(Place);

        // Close to -20; comparing the bright MCUs would pull it the wrong way.
        Assert.InRange(deltas[0], -21, -18);
        Assert.InRange(deltas[1], -1, 1);
        Assert.InRange(deltas[2], -1, 1);
    }
}
