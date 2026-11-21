namespace JpegRepair.Core;

public sealed class QuantizationTable(int id, int precision, ushort[] natural)
{
    public int Id { get; } = id;

    /// <summary>0 = 8-bit entries, 1 = 16-bit entries.</summary>
    public int Precision { get; } = precision;

    /// <summary>64 quantizer values in natural (row-major) order.</summary>
    public IReadOnlyList<ushort> Values { get; } = natural;
}
