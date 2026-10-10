using System.Text;

namespace Eunomia.Infra.Settings;

/// <summary>
/// Atomic write (section 21.4): writes to a temporary file next to the target (with a unique name, so two instances do
/// not collide), forces it to disk and swaps it in with a rename. A crash halfway leaves the previous file intact, never a
/// half-written one. On Unix the files are owner-only (0600): they hold hosts, users and SQL. A rename refused because
/// of a transient lock (antivirus, indexer, another instance reading) is retried.
/// </summary>
public static class AtomicFile
{
    private const int MoveAttempts = 10;

    public static void WriteAllText(string path, string contents, bool keepBackup = false) =>
        WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents), keepBackup);

    /// <param name="keepBackup">Copies the previous file to <c>&lt;path&gt;.bak</c> before replacing it.</param>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> contents, bool keepBackup = false)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            CreatePrivateDirectory(directory);
        }
        var temp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            if (keepBackup && File.Exists(path) && new FileInfo(path).Length > 0)
            {
                Retry(() => File.Copy(path, path + ".bak", overwrite: true));
            }
            Retry(() => File.Move(temp, path, overwrite: true));
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>Creates the folder (and any missing parents) owner-only on Unix (0700).</summary>
    public static void CreatePrivateDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }
}
