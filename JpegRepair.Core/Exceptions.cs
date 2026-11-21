namespace JpegRepair.Core;

/// <summary>The file is not a JPEG, or its headers are damaged beyond what the parser can work around.</summary>
public class JpegFormatException(string message) : Exception(message);

/// <summary>The file is a valid JPEG, but uses a feature this library does not handle yet (progressive, 12-bit, CMYK...).</summary>
public sealed class JpegNotSupportedException(string message) : JpegFormatException(message);

/// <summary>The (edited) coefficients cannot be written with the file's existing Huffman tables.</summary>
public sealed class JpegEncodeException(string message) : Exception(message);
