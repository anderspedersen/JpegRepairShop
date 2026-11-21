using System.Runtime.Intrinsics;

namespace JpegRepair.Core;

/// <summary>
/// Separable floating-point 8x8 inverse DCT.
/// Fast paths: DC-only blocks become a flat fill; rows whose coefficients are all zero are skipped in both passes
/// (typical photo blocks have only 1–3 non-zero rows); with AVX the 8 columns are processed as one 256-bit vector.
/// The vector and scalar paths add the same terms in the same order, so they give identical results.
/// </summary>
internal static class Idct
{
    // Basis[u * 8 + x] = C(u) / 2 * cos((2x + 1) u π / 16), with C(0) = 1/√2 and C(u > 0) = 1.
    private static readonly float[] Basis = BuildBasis();
    private static readonly Vector256<float>[] BasisRows = BuildBasisRows();
    private static readonly bool UseVectors = Vector256.IsHardwareAccelerated;

    private static float[] BuildBasis()
    {
        var basis = new float[64];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 8; x++)
                basis[u * 8 + x] = (float)((u == 0 ? Math.Sqrt(0.5) : 1.0) * Math.Cos((2 * x + 1) * u * Math.PI / 16) / 2);
        return basis;
    }

    private static Vector256<float>[] BuildBasisRows()
    {
        var basis = BuildBasis();
        var rows = new Vector256<float>[8];
        for (int u = 0; u < 8; u++)
            rows[u] = Vector256.Create(new ReadOnlySpan<float>(basis, u * 8, 8));
        return rows;
    }

    /// <summary>
    /// Dequantizes <paramref name="coefficients"/> (natural order) with <paramref name="quant"/>, transforms them
    /// and writes 8x8 level-shifted, clamped samples to <paramref name="dest"/> with the given row stride.
    /// </summary>
    public static void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<float> quant, Span<byte> dest, int stride)
    {
        if (coefficients.Slice(1, 63).IndexOfAnyExcept((short)0) < 0)
        {
            // DC only: every sample is the same.
            float flat = 128.5f + Basis[0] * (Basis[0] * (coefficients[0] * quant[0]));
            int value = (int)flat;
            byte b = (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
            for (int y = 0; y < 8; y++)
                dest.Slice(y * stride, 8).Fill(b);
            return;
        }

        Span<float> tmp = stackalloc float[64];
        int rows = HorizontalPass(coefficients, quant, tmp);
        var basis = Basis;

        if (UseVectors)
        {
            var zero = Vector256<int>.Zero;
            var max = Vector256.Create(255);
            for (int y = 0; y < 8; y++)
            {
                var sum = Vector256.Create(128.5f);
                for (int v = 0; v < 8; v++)
                {
                    if ((rows & (1 << v)) != 0)
                        sum += Vector256.Create(basis[v * 8 + y]) * Vector256.Create((ReadOnlySpan<float>)tmp.Slice(v * 8, 8));
                }

                var samples = Vector256.Min(Vector256.Max(Vector256.ConvertToInt32(sum), zero), max);
                var row = dest.Slice(y * stride, 8);
                for (int x = 0; x < 8; x++)
                    row[x] = (byte)samples.GetElement(x);
            }

            return;
        }

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                float sum = 128.5f;
                for (int v = 0; v < 8; v++)
                {
                    if ((rows & (1 << v)) != 0)
                        sum += basis[v * 8 + y] * tmp[v * 8 + x];
                }

                int sample = (int)sum;
                dest[y * stride + x] = (byte)(sample < 0 ? 0 : sample > 255 ? 255 : sample);
            }
        }
    }

    /// <summary>Same as <see cref="Transform"/> but writes unclamped, unrounded level-shifted samples (for analysis).</summary>
    public static void TransformToFloat(ReadOnlySpan<short> coefficients, ReadOnlySpan<float> quant, Span<float> dest, int stride)
    {
        Span<float> tmp = stackalloc float[64];
        int rows = HorizontalPass(coefficients, quant, tmp);
        var basis = Basis;

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                float sum = 128f;
                for (int v = 0; v < 8; v++)
                {
                    if ((rows & (1 << v)) != 0)
                        sum += basis[v * 8 + y] * tmp[v * 8 + x];
                }

                dest[y * stride + x] = sum;
            }
        }
    }

    /// <summary>tmp[v, x] = Σu basis[u, x] · F[v, u] · Q[v, u]. Returns a bit mask of the rows v that are not all zero.</summary>
    private static int HorizontalPass(ReadOnlySpan<short> coefficients, ReadOnlySpan<float> quant, Span<float> tmp)
    {
        var basis = Basis;
        int mask = 0;
        for (int v = 0; v < 8; v++)
        {
            int row = v * 8;
            var input = coefficients.Slice(row, 8);
            int last = input.LastIndexOfAnyExcept((short)0);
            if (last < 0)
                continue;
            mask |= 1 << v;

            if (UseVectors)
            {
                var sum = Vector256<float>.Zero;
                for (int u = 0; u <= last; u++)
                {
                    if (input[u] != 0)
                        sum += BasisRows[u] * Vector256.Create(input[u] * quant[row + u]);
                }

                sum.CopyTo(tmp.Slice(row, 8));
                continue;
            }

            for (int x = 0; x < 8; x++)
            {
                float sum = 0;
                for (int u = 0; u <= last; u++)
                {
                    if (input[u] != 0)
                        sum += basis[u * 8 + x] * (input[u] * quant[row + u]);
                }

                tmp[row + x] = sum;
            }
        }

        return mask;
    }
}
