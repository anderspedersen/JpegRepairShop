namespace JpegRepair.Core;

/// <summary>MSB-first bit writer that byte-stuffs (0xFF → 0xFF 0x00) as it goes.</summary>
internal sealed class BitWriter(int capacity)
{
    private readonly MemoryStream _out = new(capacity);
    private uint _buffer;
    private int _count;

    /// <summary>Writes the low <paramref name="length"/> bits (0..16) of <paramref name="bits"/>.</summary>
    public void Write(int bits, int length)
    {
        if (length == 0)
            return;

        _buffer = (_buffer << length) | ((uint)bits & ((1u << length) - 1));
        _count += length;
        while (_count >= 8)
        {
            _count -= 8;
            byte b = (byte)(_buffer >> _count);
            _out.WriteByte(b);
            if (b == 0xFF)
                _out.WriteByte(0x00);
        }

        _buffer &= (1u << _count) - 1;
    }

    /// <summary>Pads the last partial byte with 1-bits, as the standard requires before a marker.</summary>
    public void FlushWithOnes()
    {
        if (_count > 0)
            Write((1 << (8 - _count)) - 1, 8 - _count);
    }

    /// <summary>Writes a marker. Call <see cref="FlushWithOnes"/> first.</summary>
    public void WriteMarker(byte marker)
    {
        _out.WriteByte(0xFF);
        _out.WriteByte(marker);
    }

    public byte[] ToArray() => _out.ToArray();
}
