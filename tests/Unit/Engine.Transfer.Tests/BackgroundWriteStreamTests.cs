namespace Eunomia.Engine.Transfer.Tests;

/// <summary>
/// The writer of the exports' file and ZIP: the bytes reach the inner stream in order, on another thread, with at most
/// <c>depth</c> blocks waiting; an error of the inner stream comes out unchanged to the caller.
/// </summary>
public sealed class BackgroundWriteStreamTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task WritesEverythingInOrder()
    {
        var inner = new MemoryStream();
        var expected = new MemoryStream();
        using (var stream = new BackgroundWriteStream(inner, leaveOpen: true, blockSize: 16, depth: 2))
        {
            foreach (var length in new[] { 1, 7, 16, 15, 33, 0, 4096, 3 })
            {
                var bytes = Bytes(length, length);
                stream.Write(bytes);
                expected.Write(bytes);
            }
            stream.WriteByte(42);
            expected.WriteByte(42);
            await stream.WriteAsync(new byte[] { 1, 2, 3 }, Ct);
            expected.Write([1, 2, 3]);
            stream.Write(new byte[] { 9, 8, 7, 6 }, 1, 2);
            expected.Write([8, 7]);
        }
        Assert.Equal(expected.ToArray(), inner.ToArray());
        Assert.True(inner.CanWrite, "leaveOpen: the inner stream stays open.");
    }

    [Fact]
    public void WritesOnAThreadOfItsOwn()
    {
        var inner = new RecordingStream();
        using (var stream = new BackgroundWriteStream(inner, blockSize: 4))
        {
            stream.Write(Bytes(64, 1));
        }
        Assert.NotEmpty(inner.Threads);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, inner.Threads);
        Assert.True(inner.Disposed);
    }

    [Fact]
    public async Task TheCallerGetsAheadByAtMostDepthBlocks()
    {
        var inner = new RecordingStream { Gate = new ManualResetEventSlim(false) };
        using var stream = new BackgroundWriteStream(inner, blockSize: 8, depth: 2);
        var accepted = 0;
        var writer = Task.Run(
            () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    stream.Write(Bytes(8, i));
                    Interlocked.Increment(ref accepted);
                }
            },
            Ct);

        // The first block is being written (and waits at the gate), two more wait in the queue: the fourth write waits.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref accepted) == 3, Patience));
        await Task.Delay(200, Ct);
        Assert.Equal(3, Volatile.Read(ref accepted));
        Assert.False(writer.IsCompleted);

        inner.Gate.Set();
        await writer.WaitAsync(Patience, Ct);
        stream.Flush();
        Assert.Equal(Enumerable.Range(0, 10).SelectMany(i => Bytes(8, i)), inner.Written.ToArray());
    }

    [Fact]
    public async Task FlushWaitsForTheWritesAndFlushesTheInnerStream()
    {
        var inner = new RecordingStream { Gate = new ManualResetEventSlim(false) };
        using var stream = new BackgroundWriteStream(inner, blockSize: 1024);
        stream.Write([1, 2, 3]);
        Assert.Equal(0, inner.Written.Length);

        var flush = Task.Run(stream.Flush, Ct);
        await Task.Delay(100, Ct);
        Assert.False(flush.IsCompleted);
        inner.Gate.Set();
        await flush.WaitAsync(Patience, Ct);

        Assert.Equal(new byte[] { 1, 2, 3 }, inner.Written.ToArray());
        Assert.Equal(1, inner.Flushes);
    }

    [Fact]
    public void AnErrorOfTheInnerStreamComesOutUnchanged()
    {
        var full = new LocalFileException(new IOException("There is not enough space on the disk."));
        var inner = new RecordingStream { FailAt = 2, Error = full };
        var stream = new BackgroundWriteStream(inner, blockSize: 4, depth: 1);

        // The error happens on the thread; the caller sees it at the latest when it flushes.
        var error = Record.Exception(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                stream.Write(Bytes(4, i));
            }
            stream.Flush();
        });
        Assert.Same(full, error);
        Assert.Same(full, Assert.Throws<LocalFileException>(() => stream.Write([1])));
        Assert.Same(full, Assert.Throws<LocalFileException>(stream.Flush));

        // Closing reports it too, and still closes the inner stream; a second close does nothing.
        Assert.Same(full, Assert.Throws<LocalFileException>(stream.Dispose));
        Assert.True(inner.Disposed);
        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.Write([1]));

        // Nothing after the failed block was written.
        Assert.Equal(Bytes(4, 0), inner.Written.ToArray());
    }

    [Fact]
    public void AnErrorWhenClosingTheInnerStreamComesOut()
    {
        var inner = new RecordingStream { DisposeError = new LocalFileException(new IOException("The network path was not found.")) };
        var stream = new BackgroundWriteStream(inner, blockSize: 4);
        stream.Write([1, 2, 3, 4, 5]);

        Assert.Same(inner.DisposeError, Assert.Throws<LocalFileException>(stream.Dispose));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, inner.Written.ToArray());
    }

    [Fact]
    public void TheFirstErrorWinsOverTheOneWhenClosing()
    {
        var first = new LocalFileException(new IOException("first"));
        var inner = new RecordingStream { FailAt = 1, Error = first, DisposeError = new LocalFileException(new IOException("second")) };
        var stream = new BackgroundWriteStream(inner, blockSize: 4);
        stream.Write([1, 2, 3]);

        Assert.Same(first, Assert.Throws<LocalFileException>(stream.Dispose));
        Assert.True(inner.Disposed);
    }

    [Fact]
    public void ItIsAWriteOnlyStream()
    {
        using var stream = new BackgroundWriteStream(new MemoryStream());
        Assert.True(stream.CanWrite);
        Assert.False(stream.CanRead || stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], 0, 1));
        Assert.Throws<ArgumentException>(() => new BackgroundWriteStream(new MemoryStream([], writable: false)));
        stream.Dispose();
        Assert.False(stream.CanWrite);
    }

    /// <summary>A full disk under the writer, for real: a locked region of the file fails the writes as a local file error.</summary>
    [Fact]
    public void ARealFileErrorIsALocalFileError()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Region locks only block reads and writes on Windows (elsewhere they are advisory).");
            return;
        }
        var path = Path.Combine(Path.GetTempPath(), "eunomia-tests", "background-" + Guid.NewGuid().ToString("N")[..8] + ".sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var locker = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                locker.Lock(0, 1024 * 1024);
                var stream = new BackgroundWriteStream(LocalFileStream.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1), blockSize: 1024);

                var error = Record.Exception(() =>
                {
                    stream.Write(new byte[4096]);
                    stream.Flush();
                });
                Assert.IsType<IOException>(Assert.IsType<LocalFileException>(error).InnerException);
                Assert.Same(error, Assert.Throws<LocalFileException>(stream.Dispose));
            }
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>An inner stream that records what reaches it and on which thread, and can wait, fail or fail when closing.</summary>
    private sealed class RecordingStream : Stream
    {
        private int _writes;

        public MemoryStream Written { get; } = new();

        public List<int> Threads { get; } = [];

        public ManualResetEventSlim? Gate { get; init; }

        /// <summary>The write (1-based) that throws <see cref="Error"/>.</summary>
        public int FailAt { get; init; }

        public Exception? Error { get; init; }

        public Exception? DisposeError { get; init; }

        public int Flushes { get; private set; }

        public bool Disposed { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !Disposed;

        public override long Length => Written.Length;

        public override long Position
        {
            get => Written.Length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Gate?.Wait(Patience);
            lock (Threads)
            {
                Threads.Add(Environment.CurrentManagedThreadId);
            }
            if (++_writes == FailAt)
            {
                throw Error!;
            }
            Written.Write(buffer, offset, count);
        }

        public override void Flush() => Flushes++;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
            if (DisposeError is not null)
            {
                throw DisposeError;
            }
        }
    }
}
