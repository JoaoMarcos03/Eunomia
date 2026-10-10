using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Eunomia.Engine.Results;

/// <summary>
/// Ficheiro de spill de um resultado (secção 11.3): blocos escritos uma vez, em sequência, e lidos por offset. Aberto
/// com <see cref="FileOptions.DeleteOnClose"/>, por isso desaparece ao fechar o resultado (ou o processo). Thread-safe.
/// </summary>
public sealed class SpillFile : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, (long Offset, int Length)> _index = [];
    private long _length;

    public SpillFile(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"result-{Environment.ProcessId}-{Guid.NewGuid():N}.spill");
        _handle = File.OpenHandle(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    public string Path { get; }

    /// <summary>Bytes escritos no disco.</summary>
    public long Length
    {
        get
        {
            lock (_gate)
            {
                return _length;
            }
        }
    }

    public bool Contains(int blockIndex)
    {
        lock (_gate)
        {
            return _index.ContainsKey(blockIndex);
        }
    }

    /// <summary>Escreve o bloco (uma vez; blocos são imutáveis, por isso escrever de novo não é preciso).</summary>
    public void Write(RowBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (Contains(block.BlockIndex))
        {
            return;
        }
        var bytes = Serialize(block);
        long offset;
        lock (_gate)
        {
            offset = _length;
            _length += bytes.Length;
        }
        RandomAccess.Write(_handle, bytes, offset);
        lock (_gate)
        {
            _index[block.BlockIndex] = (offset, bytes.Length);
        }
    }

    public async ValueTask<RowBlock> ReadAsync(int blockIndex, ResultSchema schema, CancellationToken cancellationToken)
    {
        (long Offset, int Length) entry;
        lock (_gate)
        {
            if (!_index.TryGetValue(blockIndex, out entry))
            {
                throw new ArgumentOutOfRangeException(nameof(blockIndex));
            }
        }
        var buffer = new byte[entry.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await RandomAccess.ReadAsync(_handle, buffer.AsMemory(read), entry.Offset + read, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("Spill file truncated.");
            }
            read += n;
        }
        return Deserialize(buffer, schema);
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>Apaga ficheiros de spill deixados por processos que já não existem (arranque, secção 11.3).</summary>
    public static int CleanOrphans(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "result-*.spill"))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException)
            {
                // Em uso por outra instância: fica.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return deleted;
    }

    private static byte[] Serialize(RowBlock block)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(block.BlockIndex);
        writer.Write(block.FirstRow);
        writer.Write(block.RowCount);
        var columns = block.Columns;
        writer.Write(columns.Length);
        foreach (var c in columns)
        {
            WriteArray(writer, c.Longs);
            WriteArray(writer, c.Doubles);
            WriteArray(writer, c.Utf8);
            WriteArray(writer, c.Offsets);
            WriteArray(writer, c.Nulls);
            WriteArray(writer, c.Truncated);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static RowBlock Deserialize(byte[] bytes, ResultSchema schema)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var blockIndex = reader.ReadInt32();
        var firstRow = reader.ReadInt64();
        var rowCount = reader.ReadInt32();
        var columns = new ColumnVector[reader.ReadInt32()];
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i] = new ColumnVector
            {
                Longs = ReadArray<long>(reader),
                Doubles = ReadArray<double>(reader),
                Utf8 = ReadArray<byte>(reader) ?? [],
                Offsets = ReadArray<int>(reader),
                Nulls = ReadArray<ulong>(reader) ?? [],
                Truncated = ReadArray<ulong>(reader) ?? [],
            };
        }
        return new RowBlock(blockIndex, firstRow, rowCount, schema, columns);
    }

    private static void WriteArray<T>(BinaryWriter writer, T[]? array)
        where T : unmanaged
    {
        if (array is null)
        {
            writer.Write(-1);
            return;
        }
        writer.Write(array.Length);
        writer.Write(MemoryMarshal.AsBytes(array.AsSpan()));
    }

    private static T[]? ReadArray<T>(BinaryReader reader)
        where T : unmanaged
    {
        var length = reader.ReadInt32();
        if (length < 0)
        {
            return null;
        }
        var array = new T[length];
        var span = MemoryMarshal.AsBytes(array.AsSpan());
        if (reader.Read(span) != span.Length)
        {
            throw new EndOfStreamException("Spill block truncated.");
        }
        return array;
    }
}
