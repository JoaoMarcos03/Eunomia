namespace Eunomia.Engine.Transfer.Tests;

/// <summary>
/// Errors of the local files of a transfer are <see cref="LocalFileException"/>s, never IOExceptions: inside a job
/// connection the driver reads an IOException as a lost connection. The failures are real ones of the operating system
/// (a file another handle holds, a locked region), not a fake stream.
/// </summary>
public sealed class LocalFileStreamTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "eunomia-tests", "local-" + Guid.NewGuid().ToString("N")[..8]);

    public LocalFileStreamTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NewFile(int length)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N")[..8] + ".bin");
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    [Fact]
    public async Task ReadsAndWritesLikeTheFile()
    {
        var path = Path.Combine(_directory, "data.bin");
        await using (var file = LocalFileStream.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1))
        {
            Assert.Equal(Path.GetFullPath(path), file.Name);
            Assert.True(file.CanRead && file.CanWrite && file.CanSeek);
            file.Write([1, 2, 3]);
            await file.WriteAsync(new byte[] { 4, 5 }, Ct);
            file.WriteByte(6);
            await file.FlushAsync(Ct);
            Assert.Equal(6, file.Length);
            Assert.Equal(6, file.Position);

            file.Position = 1;
            var buffer = new byte[8];
            Assert.Equal(2, file.Read(buffer, 0, 2));
            Assert.Equal(2, await file.ReadAsync(buffer.AsMemory(2, 2), Ct));
            Assert.Equal(6, file.ReadByte());
            Assert.Equal(-1, file.ReadByte());
            Assert.Equal(new byte[] { 2, 3, 4, 5 }, buffer[..4]);

            file.SetLength(2);
            Assert.Equal(0, file.Seek(0, SeekOrigin.Begin));
        }
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(path, Ct));
    }

    [Fact]
    public void FileInUseDoesNotOpen()
    {
        var path = NewFile(8);
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var error = Assert.Throws<LocalFileException>(() => LocalFileStream.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

        Assert.IsAssignableFrom<IOException>(error.InnerException);
        Assert.Equal(error.InnerException!.Message, error.Message);
    }

    [Fact]
    public void MissingFileDoesNotOpen()
    {
        var error = Assert.Throws<LocalFileException>(() => LocalFileStream.Open(Path.Combine(_directory, "missing.sql"), FileMode.Open, FileAccess.Read, FileShare.Read));
        Assert.IsType<FileNotFoundException>(error.InnerException);
    }

    [Fact]
    public async Task ReadsAndWritesOfALockedRegionFail()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Region locks only block reads and writes on Windows (elsewhere they are advisory).");
            return;
        }
        var path = NewFile(64);
        using var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        locker.Lock(0, 64);
        await using var file = LocalFileStream.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1);
        var buffer = new byte[16];

        Assert.IsType<IOException>(Assert.Throws<LocalFileException>(() => file.Read(buffer, 0, buffer.Length)).InnerException);
        Assert.Throws<LocalFileException>(() => file.Read(buffer.AsSpan()));
        await Assert.ThrowsAsync<LocalFileException>(async () => await file.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false, Ct));
        Assert.Throws<LocalFileException>(() => file.Write(buffer, 0, buffer.Length));
        await Assert.ThrowsAsync<LocalFileException>(async () => await file.WriteAsync(buffer, Ct));

        locker.Unlock(0, 64);
        file.Position = 0;
        Assert.Equal(buffer.Length, file.Read(buffer, 0, buffer.Length));
    }

    /// <summary>
    /// The last write happens when the output closes (a full disk shows up there): it fails as a local file error, and
    /// the file is closed anyway, so the export can delete its <c>.partial</c>.
    /// </summary>
    [Fact]
    public void OutputThatFailsWhenClosingStillClosesTheFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Region locks only block reads and writes on Windows (elsewhere they are advisory).");
            return;
        }
        var path = NewFile(0);
        using (var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            locker.Lock(0, 1024);
            var output = new Utf8Output(LocalFileStream.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1));
            output.Write("INSERT INTO t VALUES (1);");

            Assert.IsType<IOException>(Assert.Throws<LocalFileException>(output.Dispose).InnerException);
        }
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void GuardTurnsOnlyIOExceptionsIntoLocalFileErrors()
    {
        var full = new IOException("There is not enough space on the disk.");
        var error = Assert.Throws<LocalFileException>(() => LocalFileException.Guard(() => throw full));
        Assert.Same(full, error.InnerException);
        Assert.Equal(full.Message, error.Message);
        Assert.IsType<FileNotFoundException>(Assert.Throws<LocalFileException>(() => LocalFileException.Guard(() => File.Move(Path.Combine(_directory, "a"), Path.Combine(_directory, "b")))).InnerException);

        Assert.Throws<UnauthorizedAccessException>(() => LocalFileException.Guard(() => throw new UnauthorizedAccessException()));
        Assert.Throws<InvalidOperationException>(() => LocalFileException.Guard<int>(() => throw new InvalidOperationException()));
        Assert.Equal(42, LocalFileException.Guard(() => 42));
    }
}
