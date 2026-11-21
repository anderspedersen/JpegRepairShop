namespace JpegRepair.Core;

public static class Markers
{
    public const byte SOF0 = 0xC0; // Baseline DCT
    public const byte SOF1 = 0xC1; // Extended sequential DCT, Huffman
    public const byte SOF2 = 0xC2; // Progressive DCT
    public const byte DHT = 0xC4;
    public const byte JPG = 0xC8;
    public const byte DAC = 0xCC;
    public const byte RST0 = 0xD0;
    public const byte RST7 = 0xD7;
    public const byte SOI = 0xD8;
    public const byte EOI = 0xD9;
    public const byte SOS = 0xDA;
    public const byte DQT = 0xDB;
    public const byte DNL = 0xDC;
    public const byte DRI = 0xDD;
    public const byte APP0 = 0xE0;
    public const byte APP15 = 0xEF;
    public const byte COM = 0xFE;

    public static bool IsRst(byte marker) => marker is >= RST0 and <= RST7;

    public static bool IsSof(byte marker) => marker is >= 0xC0 and <= 0xCF and not DHT and not JPG and not DAC;

    public static string Name(byte marker) => marker switch
    {
        SOF0 => "SOF0",
        SOF1 => "SOF1",
        SOF2 => "SOF2",
        DHT => "DHT",
        SOI => "SOI",
        EOI => "EOI",
        SOS => "SOS",
        DQT => "DQT",
        DNL => "DNL",
        DRI => "DRI",
        COM => "COM",
        _ when IsRst(marker) => $"RST{marker - RST0}",
        _ when marker is >= APP0 and <= APP15 => $"APP{marker - APP0}",
        _ when IsSof(marker) => $"SOF{marker - 0xC0}",
        _ => $"0xFF{marker:X2}",
    };

    /// <summary>Human-readable description of a frame type, for "not supported" messages.</summary>
    public static string DescribeFrame(byte marker) => marker switch
    {
        SOF0 => "Baseline",
        SOF1 => "Extended sequential",
        SOF2 => "Progressive",
        0xC3 => "Lossless",
        >= 0xC5 and <= 0xC7 => "Hierarchical",
        >= 0xC9 and <= 0xCF => "Arithmetic-coded",
        _ => Name(marker),
    };
}
