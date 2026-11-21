namespace JpegRepair.Core;

/// <summary>A sequence of MCUs in display order, as consumed by <see cref="JpegRenderer"/>.</summary>
public interface IMcuSource
{
    /// <summary>Number of MCUs available. Can be more than the frame's MCU count (overflow) or fewer.</summary>
    int Count { get; }

    McuStatus GetStatus(int index);

    /// <summary>Quantized coefficients of one MCU in <see cref="McuLayout"/> block order, natural order within blocks.</summary>
    ReadOnlySpan<short> GetCoefficients(int index);
}
