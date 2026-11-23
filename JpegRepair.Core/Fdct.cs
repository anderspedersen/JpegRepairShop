namespace JpegRepair.Core;

/// <summary>
/// Separable floating-point 8x8 forward DCT, the exact inverse of <see cref="Idct"/> (same orthonormal basis):
/// F[v, u] = Σy Σx basis[v, y] · basis[u, x] · s[y, x], with u horizontal and v vertical frequency, natural order.
/// </summary>
internal static class Fdct
{
    // Basis[u * 8 + x] = C(u) / 2 * cos((2x + 1) u π / 16), with C(0) = 1/√2 and C(u > 0) = 1.
    private static readonly float[] Basis = BuildBasis();

    private static float[] BuildBasis()
    {
        var basis = new float[64];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 8; x++)
                basis[u * 8 + x] = (float)((u == 0 ? Math.Sqrt(0.5) : 1.0) * Math.Cos((2 * x + 1) * u * Math.PI / 16) / 2);
        return basis;
    }

    /// <summary>Transforms 8x8 samples (row-major, already level-shifted by −128) into DCT coefficients (natural order).</summary>
    public static void Forward(ReadOnlySpan<float> samples, Span<float> coefficients)
    {
        var basis = Basis;
        Span<float> tmp = stackalloc float[64]; // tmp[y * 8 + u] = Σx basis[u, x] · s[y, x]
        for (int y = 0; y < 8; y++)
        {
            for (int u = 0; u < 8; u++)
            {
                float sum = 0;
                for (int x = 0; x < 8; x++)
                    sum += basis[u * 8 + x] * samples[y * 8 + x];
                tmp[y * 8 + u] = sum;
            }
        }

        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                float sum = 0;
                for (int y = 0; y < 8; y++)
                    sum += basis[v * 8 + y] * tmp[y * 8 + u];
                coefficients[v * 8 + u] = sum;
            }
        }
    }
}
