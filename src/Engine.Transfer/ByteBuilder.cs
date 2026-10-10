using System.Text;

namespace Eunomia.Engine.Transfer;

/// <summary>UTF-8 bytes in a growing array (a row's tuple before deciding which INSERT it goes into).</summary>
internal sealed class ByteBuilder
{
    private byte[] _data;

    public ByteBuilder(int capacity = 4096) => _data = new byte[capacity];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Span => _data.AsSpan(0, Length);

    public void Clear() => Length = 0;

    public void Append(byte value)
    {
        Ensure(1);
        _data[Length++] = value;
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_data.AsSpan(Length));
        Length += bytes.Length;
    }

    public void Append(string text)
    {
        Ensure(Encoding.UTF8.GetMaxByteCount(text.Length));
        Length += Encoding.UTF8.GetBytes(text, _data.AsSpan(Length));
    }

    /// <summary>Room for at least <paramref name="size"/> bytes; then call <see cref="Advance"/>.</summary>
    public Span<byte> GetSpan(int size)
    {
        Ensure(size);
        return _data.AsSpan(Length);
    }

    public void Advance(int count) => Length += count;

    private void Ensure(int extra)
    {
        var needed = (long)Length + extra;
        if (needed <= _data.Length)
        {
            return;
        }
        var size = Math.Max(needed, Math.Min(Array.MaxLength, _data.Length * 2L));
        if (size > Array.MaxLength)
        {
            throw new InvalidOperationException("Row too large.");
        }
        Array.Resize(ref _data, (int)size);
    }
}
