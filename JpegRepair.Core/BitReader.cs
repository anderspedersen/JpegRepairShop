namespace JpegRepair.Core;

/// <summary>
/// MSB-first bit reader over de-stuffed entropy-coded data (0xFF00 already turned into 0xFF, markers removed).
/// Keeps up to 64 bits in a buffer so the hot path (peek / skip) is a shift and a compare.
/// Reading past the end returns zero bits; check <see cref="IsPastEnd"/> to detect truncation.
/// </summary>
public sealed class BitReader(byte[] data)
{
    private readonly byte[] _data = data;
    private ulong _buffer;   // next bit is bit 63
    private int _count;      // number of valid bits in _buffer
    private int _next;       // index of the next byte to load (may run past the end: zero padding)

    /// <summary>Current position in bits from the start of the data.</summary>
    public long Position
    {
        get => (long)_next * 8 - _count;
        set
        {
            _next = (int)(value >> 3);
            _buffer = 0;
            _count = 0;
            int bits = (int)(value & 7);
            if (bits > 0)
            {
                Fill();
                _buffer <<= bits;
                _count -= bits;
            }
        }
    }

    public long Length => (long)_data.Length * 8;

    public bool IsPastEnd => Position > Length;

    /// <summary>Returns the next <paramref name="count"/> bits (1..32) without consuming them.</summary>
    public int PeekBits(int count)
    {
        if (_count < count)
            Fill();
        return (int)(_buffer >> (64 - count));
    }

    /// <summary>Consumes <paramref name="count"/> bits (0..32).</summary>
    public void Skip(int count)
    {
        if (_count < count)
            Fill();
        _buffer <<= count;
        _count -= count;
    }

    public int ReadBits(int count)
    {
        if (count == 0)
            return 0;
        if (_count < count)
            Fill();
        int value = (int)(_buffer >> (64 - count));
        _buffer <<= count;
        _count -= count;
        return value;
    }

    public void AlignToByte()
    {
        int bits = _count & 7;
        _buffer <<= bits;
        _count -= bits;
    }

    private void Fill()
    {
        var data = _data;
        while (_count <= 56)
        {
            ulong b = (uint)_next < (uint)data.Length ? data[_next] : 0UL;
            _next++;
            _buffer |= b << (56 - _count);
            _count += 8;
        }
    }
}
