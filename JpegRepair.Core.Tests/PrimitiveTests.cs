namespace JpegRepair.Core.Tests;

public class PrimitiveTests
{
    // Standard luminance tables from ITU-T T.81 Annex K.
    private static readonly byte[] DcCounts = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcSymbols = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly byte[] AcCounts = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];

    private static readonly byte[] AcSymbols =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Every_symbol_survives_an_encode_decode_round_trip(int tableClass)
    {
        var table = tableClass == 0
            ? new HuffmanTable(0, 0, DcCounts, DcSymbols)
            : new HuffmanTable(1, 0, AcCounts, AcSymbols);

        var writer = new BitWriter(1024);
        foreach (var symbol in table.Symbols)
        {
            Assert.True(table.TryGetCode(symbol, out int code, out int length));
            writer.Write(code, length);
        }

        writer.FlushWithOnes();

        var reader = new BitReader(Destuff(writer.ToArray()));
        foreach (var symbol in table.Symbols)
            Assert.Equal(symbol, table.Decode(reader));
    }

    [Fact]
    public void Known_codes_match_the_standard()
    {
        var dc = new HuffmanTable(0, 0, DcCounts, DcSymbols);

        Assert.True(dc.TryGetCode(0, out int code, out int length));
        Assert.Equal((0b00, 2), (code, length));
        Assert.True(dc.TryGetCode(11, out code, out length));
        Assert.Equal((0b111111110, 9), (code, length));
    }

    [Fact]
    public void Invalid_code_returns_minus_one()
    {
        // All-ones is never a valid code in a JPEG Huffman table.
        var dc = new HuffmanTable(0, 0, DcCounts, DcSymbols);
        var reader = new BitReader([0xFF, 0xFF, 0xFF]);

        Assert.Equal(-1, dc.Decode(reader));
    }

    [Fact]
    public void Over_subscribed_table_is_rejected()
    {
        var counts = new byte[16];
        counts[0] = 3; // three codes of length 1 cannot exist
        Assert.Throws<JpegFormatException>(() => new HuffmanTable(0, 0, counts, [0, 1, 2]));
    }

    [Theory]
    [InlineData(0b0, 1, -1)]
    [InlineData(0b1, 1, 1)]
    [InlineData(0b00, 2, -3)]
    [InlineData(0b11, 2, 3)]
    [InlineData(0b0111, 4, -8)]
    [InlineData(0b1000, 4, 8)]
    public void Extend_matches_the_standard(int bits, int size, int expected)
    {
        Assert.Equal(expected, ScanDecoder.Extend(bits, size));
    }

    [Fact]
    public void Dc_only_block_gives_a_flat_block()
    {
        var coefficients = new short[64];
        coefficients[0] = 10;
        var quant = Enumerable.Repeat(8f, 64).ToArray();
        var samples = new byte[64];

        Idct.Transform(coefficients, quant, samples, 8);

        // DC 10 * q 8 = 80 → mean (80 / 8 = 10) + 128.
        Assert.All(samples, s => Assert.Equal(138, s));
    }

    [Fact]
    public void Fast_idct_matches_a_textbook_reference()
    {
        var random = new Random(42);
        var quant = Enumerable.Range(0, 64).Select(i => (float)(1 + i % 7)).ToArray();
        var samples = new byte[64];

        for (int round = 0; round < 500; round++)
        {
            // Sparse blocks like real JPEG data, plus some dense ones.
            var coefficients = new short[64];
            int count = round % 5 == 0 ? 64 : random.Next(0, 10);
            for (int i = 0; i < count; i++)
                coefficients[random.Next(64)] = (short)random.Next(-60, 61);

            Idct.Transform(coefficients, quant, samples, 8);

            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    double sum = 0;
                    for (int v = 0; v < 8; v++)
                        for (int u = 0; u < 8; u++)
                            sum += (u == 0 ? Math.Sqrt(0.5) : 1) * (v == 0 ? Math.Sqrt(0.5) : 1) / 4
                                   * coefficients[v * 8 + u] * quant[v * 8 + u]
                                   * Math.Cos((2 * x + 1) * u * Math.PI / 16) * Math.Cos((2 * y + 1) * v * Math.PI / 16);
                    int expected = Math.Clamp((int)Math.Round(sum + 128), 0, 255);
                    Assert.InRange(samples[y * 8 + x], expected - 1, expected + 1);
                }
            }
        }
    }

    [Fact]
    public void Bit_reader_seeks_to_any_bit()
    {
        var data = new byte[] { 0b1100_1010, 0b0101_0011, 0xFF, 0x00 };
        var reader = new BitReader(data);

        reader.Position = 5;
        Assert.Equal(0b010_0101, reader.ReadBits(7));
        Assert.Equal(12, reader.Position);
        reader.AlignToByte();
        Assert.Equal(16, reader.Position);
        Assert.Equal(0xFF, reader.ReadBits(8));
    }

    [Fact]
    public void Bit_reader_reads_msb_first_and_pads_with_zeros()
    {
        var reader = new BitReader([0b1010_0000]);

        Assert.Equal(0b101, reader.ReadBits(3));
        Assert.Equal(0, reader.ReadBits(8));
        Assert.True(reader.IsPastEnd);
    }

    private static byte[] Destuff(byte[] stuffed)
    {
        var result = new List<byte>();
        for (int i = 0; i < stuffed.Length; i++)
        {
            result.Add(stuffed[i]);
            if (stuffed[i] == 0xFF && i + 1 < stuffed.Length && stuffed[i + 1] == 0)
                i++;
        }

        return [.. result];
    }
}
