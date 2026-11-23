namespace JpegRepair.Core.Tests;

public class FillTests
{
    // photo.JPG: 100 × 150 MCUs of 16 × 8; MCU 4037 is row 40, column 37 (as in the other tests).
    private const int Place = 4037;
    private const int PerRow = 100;

    private static JpegDocument LoadPhoto() => JpegDocument.Decode(TestFiles.Read(TestFiles.Photo));

    /// <summary>Mean absolute difference per channel (B, G, R) between two renders, over one MCU.</summary>
    private static double McuDifference(JpegDocument a, byte[] reference, int index)
    {
        var frame = a.Frame;
        var pixels = a.Pixels;
        int x0 = index % frame.McusPerRow * frame.McuWidth, y0 = index / frame.McusPerRow * frame.McuHeight;
        double sum = 0;
        int count = 0;
        for (int y = y0; y < y0 + frame.McuHeight; y++)
        {
            for (int x = x0; x < x0 + frame.McuWidth; x++)
            {
                int o = (y * frame.Width + x) * 4;
                for (int c = 0; c < 3; c++)
                    sum += Math.Abs(pixels[o + c] - reference[o + c]);
                count += 3;
            }
        }

        return sum / count;
    }

    [Fact]
    public void Forward_dct_is_the_inverse_of_the_renderer_transform()
    {
        var random = new Random(1);
        var samples = new float[64];
        for (int i = 0; i < 64; i++)
            samples[i] = random.Next(256) - 128;

        var coefficients = new float[64];
        Fdct.Forward(samples, coefficients);

        // Back with a quantization table of 1s, at a scale where rounding to short is fine.
        var quantized = coefficients.Select(c => (short)MathF.Round(c * 16)).ToArray();
        var quant = Enumerable.Repeat(1f / 16, 64).ToArray();
        var back = new float[64];
        Idct.TransformToFloat(quantized, quant, back, 8);

        for (int i = 0; i < 64; i++)
            Assert.InRange(back[i] - 128, samples[i] - 0.3f, samples[i] + 0.3f);
    }

    [Fact]
    public void Garbage_mcu_is_filled_close_to_the_original()
    {
        var reference = LoadPhoto().RenderBgra();
        var doc = LoadPhoto();

        // MCU 4037 alone becomes much brighter (+40 DC steps ≈ +25 levels), like garbage.
        doc.AdjustDc(Place, [40, 0, 0]);
        doc.AdjustDc(Place + 1, [-40, 0, 0]);
        double damaged = McuDifference(doc, reference, Place);

        Assert.Equal(1, doc.FillMcus([Place]));

        double filled = McuDifference(doc, reference, Place);
        Assert.Equal(McuStatus.Filled, doc.GetStatus(Place));
        Assert.True(filled < damaged / 2, $"damaged {damaged:0.0}, filled {filled:0.0}");
    }

    [Fact]
    public void Fill_targets_are_bad_corrupt_and_blank_mcus()
    {
        var doc = LoadPhoto();
        doc.InsertBlank(Place);
        doc.Remove(Place + 1); // the blank replaces MCU 4037; everything else stays in place
        doc.SetMark(Place + 10, McuMark.Bad);

        var targets = doc.FindFillTargets();

        Assert.Contains(Place, targets);
        Assert.Contains(Place + 10, targets);
    }

    [Fact]
    public void Filled_blank_is_no_longer_blank_and_follows_colour_fixes()
    {
        var doc = LoadPhoto();
        doc.InsertBlank(Place);
        doc.Remove(Place + 1);

        doc.FillMcus([Place]);

        var entry = doc[Place];
        Assert.Equal(McuStatus.Filled, entry.Status);
        Assert.False(entry.IsBlank);
        Assert.True(entry.IsInserted);

        short before = doc.GetDc(Place, 0);
        doc.AdjustDc(Place - 5, [3, 0, 0]);
        Assert.Equal(before + 3, doc.GetDc(Place, 0));
    }

    [Fact]
    public void Fill_is_one_undo_step()
    {
        var doc = LoadPhoto();
        var original = doc.GetCoefficients(Place).ToArray();
        var originalNext = doc.GetCoefficients(Place + 1).ToArray();

        doc.FillMcus([Place, Place + 1]);
        Assert.Equal("Fill 2 MCUs", doc.UndoDescription);
        Assert.Equal(McuStatus.Filled, doc.GetStatus(Place + 1));

        doc.Undo();
        Assert.Equal(McuStatus.Ok, doc.GetStatus(Place));
        Assert.Equal(original, doc.GetCoefficients(Place).ToArray());
        Assert.Equal(originalNext, doc.GetCoefficients(Place + 1).ToArray());

        doc.Redo();
        Assert.Equal(McuStatus.Filled, doc.GetStatus(Place));
    }

    [Fact]
    public void Filled_picture_saves_and_decodes_to_the_same_coefficients()
    {
        var doc = LoadPhoto();
        int[] targets = [Place, Place + 1, Place + PerRow, Place + PerRow + 1, 0, PerRow - 1];
        doc.FillMcus(targets);

        var saved = JpegDocument.Decode(doc.Save());

        foreach (int i in targets)
            Assert.Equal(doc.GetCoefficients(i).ToArray(), saved.GetCoefficients(i).ToArray());
        Assert.Equal(doc.GetCoefficients(Place + 2).ToArray(), saved.GetCoefficients(Place + 2).ToArray());
    }

    [Fact]
    public void Plan_does_not_apply_after_an_edit()
    {
        var doc = LoadPhoto();
        var plan = doc.PrepareFill([Place]);
        doc.AdjustDc(Place, [1, 0, 0]);

        Assert.Throws<InvalidOperationException>(() => doc.ApplyFill(plan));
    }

    [Fact]
    public void Ac_values_are_fitted_to_a_table_without_some_codes()
    {
        // AC table with only EOB, (0, 1), (0, 2) and (1, 1).
        var counts = new byte[16];
        counts[2] = 4; // four codes of 3 bits
        var table = new HuffmanTable(1, 0, counts, [0x00, 0x01, 0x02, 0x11]);

        var block = new short[64];
        var zigzag = ZigZag.ToNatural;
        block[zigzag[1]] = 5;   // size 3: not in the table, becomes 3 (size 2)
        block[zigzag[3]] = -1;  // after 1 zero: (1, 1) is in the table
        block[zigzag[10]] = 1;  // after 6 zeros: (6, 1) is not, becomes 0
        block[zigzag[11]] = 2;  // after 7 zeros (the dropped value counts as one): (7, 2) is not, becomes 0

        JpegDocument.FitAcToTable(block, table);

        Assert.Equal(3, block[zigzag[1]]);
        Assert.Equal(-1, block[zigzag[3]]);
        Assert.Equal(0, block[zigzag[10]]);
        Assert.Equal(0, block[zigzag[11]]);
    }

    [Fact]
    public void Dc_is_moved_to_the_nearest_encodable_value()
    {
        // DC table with sizes 0..3 only (differences up to ±7).
        var counts = new byte[16];
        counts[2] = 4;
        var table = new HuffmanTable(0, 0, counts, [0, 1, 2, 3]);

        Assert.Equal(17, JpegDocument.FitDcToTable(17, previous: 10, next: null, table));
        Assert.Equal(17, JpegDocument.FitDcToTable(30, previous: 10, next: null, table));
        Assert.Equal(15, JpegDocument.FitDcToTable(30, previous: 10, next: 8, table));
    }
}
