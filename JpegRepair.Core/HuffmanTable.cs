namespace JpegRepair.Core;

/// <summary>
/// Canonical JPEG Huffman table with a 9-bit lookup table for decoding and a symbol → code map for encoding.
/// </summary>
public sealed class HuffmanTable
{
    internal const int LookupBits = 9;

    // (codeLength << 8) | symbol for every 9-bit prefix that starts with a code of length <= 9; 0 otherwise.
    private readonly ushort[] _lookup = new ushort[1 << LookupBits];
    private readonly int[] _maxCode = new int[17];   // indexed by code length; -1 if there are no codes of that length
    private readonly int[] _valueOffset = new int[17];
    private readonly ushort[] _encodeCode = new ushort[256];
    private readonly byte[] _encodeLength = new byte[256];
    private readonly byte[] _symbols;

    public HuffmanTable(int tableClass, int id, byte[] counts, byte[] symbols)
    {
        if (counts.Length != 16)
            throw new ArgumentException("Huffman table needs 16 length counts.", nameof(counts));

        int total = 0;
        foreach (var c in counts) total += c;
        if (total != symbols.Length || total > 256)
            throw new JpegFormatException($"Huffman table {id} declares {total} codes but has {symbols.Length} symbols.");

        TableClass = tableClass;
        Id = id;
        Counts = counts;
        _symbols = symbols;

        int code = 0, k = 0;
        for (int length = 1; length <= 16; length++)
        {
            _valueOffset[length] = k - code;
            for (int i = 0; i < counts[length - 1]; i++, k++, code++)
            {
                if (code >= 1 << length)
                    throw new JpegFormatException($"Huffman table {id} is over-subscribed (more codes than fit in {length} bits).");

                byte symbol = symbols[k];
                if (_encodeLength[symbol] == 0)
                {
                    _encodeCode[symbol] = (ushort)code;
                    _encodeLength[symbol] = (byte)length;
                }

                if (length <= LookupBits)
                {
                    int shift = LookupBits - length;
                    int first = code << shift;
                    ushort entry = (ushort)((length << 8) | symbol);
                    for (int j = 0; j < 1 << shift; j++)
                        _lookup[first + j] = entry;
                }
            }

            _maxCode[length] = counts[length - 1] == 0 ? -1 : code - 1;
            code <<= 1;
        }
    }

    /// <summary>0 = DC, 1 = AC.</summary>
    public int TableClass { get; }

    public int Id { get; }

    /// <summary>Number of codes of each length 1..16.</summary>
    public IReadOnlyList<byte> Counts { get; }

    public IReadOnlyList<byte> Symbols => _symbols;

    /// <summary>Decodes one symbol. Returns -1 if the bits do not form a valid code (a sign of corruption).</summary>
    public int Decode(BitReader reader)
    {
        int entry = _lookup[reader.PeekBits(LookupBits)];
        if (entry != 0)
        {
            reader.Skip(entry >> 8);
            return entry & 0xFF;
        }

        int bits = reader.PeekBits(16);
        for (int length = LookupBits + 1; length <= 16; length++)
        {
            int code = bits >> (16 - length);
            if (code <= _maxCode[length])
            {
                int index = code + _valueOffset[length];
                if ((uint)index >= (uint)_symbols.Length)
                    return -1;
                reader.Skip(length);
                return _symbols[index];
            }
        }

        return -1;
    }

    public bool TryGetCode(int symbol, out int code, out int length)
    {
        if ((uint)symbol > 255 || _encodeLength[symbol] == 0)
        {
            code = length = 0;
            return false;
        }

        code = _encodeCode[symbol];
        length = _encodeLength[symbol];
        return true;
    }
}
