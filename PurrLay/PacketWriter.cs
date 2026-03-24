namespace PurrLay;

/// <summary>
/// Simple byte buffer writer to replace LiteNetLib's NetDataWriter in transport-agnostic code.
/// </summary>
public class PacketWriter
{
    private byte[] _buffer = new byte[256];
    private int _position;

    public void Reset()
    {
        _position = 0;
    }

    public void Put(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    public void Put(int value)
    {
        EnsureCapacity(4);
        _buffer[_position++] = (byte)value;
        _buffer[_position++] = (byte)(value >> 8);
        _buffer[_position++] = (byte)(value >> 16);
        _buffer[_position++] = (byte)(value >> 24);
    }

    public void Put(byte[] data, int offset, int count)
    {
        EnsureCapacity(count);
        Buffer.BlockCopy(data, offset, _buffer, _position, count);
        _position += count;
    }

    public ReadOnlySpan<byte> AsReadOnlySpan()
    {
        return new ReadOnlySpan<byte>(_buffer, 0, _position);
    }

    private void EnsureCapacity(int additional)
    {
        var required = _position + additional;
        if (required <= _buffer.Length)
            return;

        var newSize = Math.Max(_buffer.Length * 2, required);
        var newBuffer = new byte[newSize];
        Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _position);
        _buffer = newBuffer;
    }
}
