namespace Eunomia.Infra.Settings;

/// <summary>
/// Onde ficam os dados da aplicação (secção 21.4). Windows: settings, sessões e <c>app.db</c> em
/// <c>%APPDATA%\Eunomia</c>; journals, layouts e spill em <c>%LOCALAPPDATA%\Eunomia</c>. macOS: tudo em
/// <c>~/Library/Application Support/Eunomia</c>. Linux (XDG): settings em <c>$XDG_CONFIG_HOME/Eunomia</c>
/// (<c>~/.config</c>), o resto em <c>$XDG_DATA_HOME/Eunomia</c> (<c>~/.local/share</c>). Portable (equivalente ao
/// <c>portable_settings.txt</c> do HeidiSQL): se existir <c>portable_settings.json</c> ao lado do executável, tudo
/// fica nessa pasta. Em macOS e Linux as pastas ficam só para o dono (0700).
/// </summary>
public sealed record AppDataPaths(string Roaming, string Local, bool IsPortable)
{
    public const string AppFolder = "Eunomia";
    public const string PortableMarker = "portable_settings.json";

    public string SettingsFile => IsPortable ? Path.Combine(Roaming, PortableMarker) : Path.Combine(Roaming, "settings.json");

    public string AppDatabase => Path.Combine(Roaming, "app.db");

    public string SessionsFile => Path.Combine(Roaming, "sessions.json");

    /// <summary>Segredos grandes cifrados (DPAPI em Windows; chaves e certificados, secção 23.2).</summary>
    public string Secrets => Path.Combine(Roaming, "secrets");

    /// <summary>Journals das query tabs (secção 12, PR-21).</summary>
    public string TabsJournal => Path.Combine(Local, "tabs");

    /// <summary>Journals de jobs de import/export (secção 13.3).</summary>
    public string JobsJournal => Path.Combine(Local, "jobs");

    public string Spill => Path.Combine(Local, "spill");

    public string Logs => Path.Combine(Local, "logs");

    /// <summary>Código de objetos que não voltaram a ser criados depois de uma gravação falhada.</summary>
    public string Recovery => Path.Combine(Local, "recovery");

    public static AppDataPaths Resolve(string executableDirectory)
    {
        if (File.Exists(Path.Combine(executableDirectory, PortableMarker)))
        {
            return new AppDataPaths(executableDirectory, Path.Combine(executableDirectory, "data"), IsPortable: true);
        }
        var (roaming, local) = PlatformFolders(Environment.GetEnvironmentVariable);
        return new AppDataPaths(Path.Combine(roaming, AppFolder), Path.Combine(local, AppFolder), IsPortable: false);
    }

    /// <summary>Pastas base do sistema (sem a pasta da aplicação): roaming (settings) e local (dados).</summary>
    public static (string Roaming, string Local) PlatformFolders(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (OperatingSystem.IsWindows())
        {
            return (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }
        var home = environment("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            var support = Path.Combine(home, "Library", "Application Support");
            return (support, support);
        }
        // XDG: só caminhos absolutos contam (a especificação manda ignorar os relativos).
        static string? Absolute(string? value) => value is { Length: > 0 } && Path.IsPathRooted(value) ? value : null;
        var config = Absolute(environment("XDG_CONFIG_HOME")) ?? Path.Combine(home, ".config");
        var data = Absolute(environment("XDG_DATA_HOME")) ?? Path.Combine(home, ".local", "share");
        return (config, data);
    }

    /// <summary>Cria as pastas que ainda não existem (em Unix, só para o dono; as que já existiam passam a 0700).</summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[] { Roaming, Local, TabsJournal, JobsJournal, Spill, Logs })
        {
            AtomicFile.CreatePrivateDirectory(dir);
            if (!OperatingSystem.IsWindows() && !IsPortable)
            {
                try
                {
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
