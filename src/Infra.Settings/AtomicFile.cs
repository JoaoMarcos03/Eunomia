using System.Text;

namespace Eunomia.Infra.Settings;

/// <summary>
/// Escrita atómica (secção 21.4): escreve num ficheiro temporário ao lado (com nome único, para duas instâncias não
/// colidirem), força para disco e troca com um rename. Um crash a meio deixa o ficheiro anterior intacto, nunca um
/// ficheiro meio escrito. Em Unix os ficheiros ficam só para o dono (0600): têm hosts, utilizadores e SQL. Um rename
/// recusado por um bloqueio passageiro (antivírus, indexador, outra instância a ler) tenta outra vez.
/// </summary>
public static class AtomicFile
{
    private const int MoveAttempts = 10;

    public static void WriteAllText(string path, string contents, bool keepBackup = false) =>
        WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents), keepBackup);

    /// <param name="keepBackup">Copia o ficheiro anterior para <c>&lt;path&gt;.bak</c> antes de o trocar.</param>
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

    /// <summary>Cria a pasta (e as que faltam) só para o dono em Unix (0700).</summary>
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
