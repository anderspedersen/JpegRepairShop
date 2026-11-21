using System.Numerics;

namespace JpegRepair.Core;

/// <summary>
/// Re-encodes coefficients with the file's own Huffman tables (and restart markers, if the file uses them).
/// For an unedited, intact file the output is identical to the original scan data.
/// </summary>
public static class ScanEncoder
{
    /// <param name="coefficients">MCU-major coefficients in the same layout as <see cref="ScanDecodeResult.Coefficients"/>;
    /// the number of MCUs is taken from its length.</param>
    public static byte[] Encode(JpegFile file, McuLayout layout, ReadOnlySpan<short> coefficients)
    {
        int perMcu = layout.CoefficientsPerMcu;
        if (coefficients.Length % perMcu != 0)
            throw new ArgumentException("Coefficient count is not a whole number of MCUs.", nameof(coefficients));

        int mcuCount = coefficients.Length / perMcu;
        var (dc, ac) = layout.ResolveHuffmanTables(file);
        var blocks = layout.BlockArray;
        var zigzag = ZigZag.ToNatural;
        var writer = new BitWriter(file.Entropy.Bytes.Length + 1024);
        var predictors = new int[layout.Frame.Components.Count];
        int interval = file.RestartInterval;

        for (int m = 0; m < mcuCount; m++)
        {
            if (interval > 0 && m > 0 && m % interval == 0)
            {
                writer.FlushWithOnes();
                writer.WriteMarker((byte)(Markers.RST0 + ((m / interval - 1) & 7)));
                Array.Clear(predictors);
            }

            for (int b = 0; b < blocks.Length; b++)
            {
                var block = coefficients.Slice((m * blocks.Length + b) * 64, 64);
                int component = blocks[b].ComponentIndex;

                // Clamp to the baseline DC range so a large manual DC adjustment can't make saving fail.
                int dcValue = Math.Clamp((int)block[0], -1024, 1023);
                int diff = dcValue - predictors[component];
                predictors[component] = dcValue;
                int size = BitSize(diff);
                Emit(writer, dc[b], size, m);
                writer.Write(diff < 0 ? diff - 1 : diff, size);

                int run = 0;
                for (int k = 1; k < 64; k++)
                {
                    int value = block[zigzag[k]];
                    if (value == 0)
                    {
                        run++;
                        continue;
                    }

                    while (run > 15)
                    {
                        Emit(writer, ac[b], 0xF0, m);
                        run -= 16;
                    }

                    size = BitSize(value);
                    if (size > 10)
                        throw new JpegEncodeException($"MCU {m}: AC coefficient {value} is too large for baseline JPEG.");
                    Emit(writer, ac[b], (run << 4) | size, m);
                    writer.Write(value < 0 ? value - 1 : value, size);
                    run = 0;
                }

                if (run > 0)
                    Emit(writer, ac[b], 0x00, m);
            }
        }

        writer.FlushWithOnes();
        return writer.ToArray();
    }

    private static int BitSize(int value)
    {
        uint magnitude = (uint)Math.Abs(value);
        return magnitude == 0 ? 0 : 32 - BitOperations.LeadingZeroCount(magnitude);
    }

    private static void Emit(BitWriter writer, HuffmanTable table, int symbol, int mcu)
    {
        if (!table.TryGetCode(symbol, out int code, out int length))
        {
            string kind = table.TableClass == 0 ? "DC" : "AC";
            throw new JpegEncodeException(
                $"MCU {mcu}: symbol 0x{symbol:X2} is not in {kind} Huffman table {table.Id}. The edit needs new Huffman tables.");
        }

        writer.Write(code, length);
    }
}
