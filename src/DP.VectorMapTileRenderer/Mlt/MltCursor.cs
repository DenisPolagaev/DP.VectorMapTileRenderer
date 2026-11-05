using System.Buffers.Binary;
using System.Text;

namespace DP.VectorMapTileRenderer.Mlt;

/// <summary>Sequential reader over the MLT byte layout (varints, strings, raw slices).</summary>
internal ref struct MltCursor(ReadOnlySpan<byte> buffer, int position = 0)
{
    private readonly ReadOnlySpan<byte> _buffer = buffer;
    private int _position = position;

    public readonly bool IsEmpty => _position >= _buffer.Length;

    public readonly int Remaining => _buffer.Length - _position;

    public byte ReadByte()
    {
        if (_position >= _buffer.Length)
        {
            throw new MltFormatException("Unexpected end of MLT tile.");
        }

        return _buffer[_position++];
    }

    public ulong ReadVarInt()
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var current = ReadByte();
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new MltFormatException("MLT varint is longer than 64 bits.");
            }
        }
    }

    public int ReadVarInt32() => checked((int)ReadVarInt());

    public string ReadString()
    {
        var length = ReadVarInt32();
        return ReadUtf8(length);
    }

    public string ReadUtf8(int length)
    {
        var slice = Take(length);
        return Encoding.UTF8.GetString(slice);
    }

    public ReadOnlySpan<byte> Take(int length)
    {
        if (length < 0 || length > Remaining)
        {
            throw new MltFormatException($"MLT stream asks for {length} bytes but only {Remaining} remain.");
        }

        var slice = _buffer.Slice(_position, length);
        _position += length;
        return slice;
    }

    public void Skip(int length) => Take(length);

    public float ReadSingleLittleEndian() => BinaryPrimitives.ReadSingleLittleEndian(Take(sizeof(float)));

    public double ReadDoubleLittleEndian() => BinaryPrimitives.ReadDoubleLittleEndian(Take(sizeof(double)));
}
