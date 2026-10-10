namespace Eunomia.Infra.Settings;

/// <summary>
/// Where the application's data lives (section 21.4). Windows: settings, sessions and <c>app.db</c> in
/// <c>%APPDATA%\Eunomia</c>; journals, layouts and spill in <c>%LOCALAPPDATA%\Eunomia</c>. macOS: everything in
/// <c>~/Library/Application Support/Eunomia</c>. Linux (XDG): settings in <c>$XDG_CONFIG_HOME/Eunomia</c>
/// (<c>~/.config</c>), the rest in <c>$XDG_DATA_HOME/Eunomia</c> (<c>~/.local/share</c>). Portable (the equivalent of
/// HeidiSQL's <c>portable_settings.txt</c>): if <c>portable_settings.json</c> exists next to the executable, everything
/// stays in that folder. On macOS and Linux the folders are owner-only (0700).
/// </summary>
public sealed record AppDataPaths(string Roaming, string Local, bool IsPortable)
{
    public const string AppFolder = "Eunomia";
    public const string PortableMarker = "portable_settings.json";

    public string SettingsFile => IsPortable ? Path.Combine(Roaming, PortableMarker) : Path.Combine(Roaming, "settings.json");

    public string AppDatabase => Path.Combine(Roaming, "app.db");

    public string SessionsFile => Path.Combine(Roaming, "sessions.json");

    /// <summary>Large encrypted secrets (DPAPI on Windows; keys and certificates, section 23.2).</summary>
    public string Secrets => Path.Combine(Roaming, "secrets");

    /// <summary>Query tab journals (section 12, PR-21).</summary>
    public string TabsJournal => Path.Combine(Local, "tabs");

    /// <summary>Import/export job journals (section 13.3).</summary>
    public string JobsJournal => Path.Combine(Local, "jobs");

    public string Spill => Path.Combine(Local, "spill");

    public string Logs => Path.Combine(Local, "logs");

    /// <summary>Code of objects that were not created again after a failed save.</summary>
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

    /// <summary>The system's base folders (without the application folder): roaming (settings) and local (data).</summary>
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
        // XDG: only absolute paths count (the specification says to ignore relative ones).
        static string? Absolute(string? value) => value is { Length: > 0 } && Path.IsPathRooted(value) ? value : null;
        var config = Absolute(environment("XDG_CONFIG_HOME")) ?? Path.Combine(home, ".config");
        var data = Absolute(environment("XDG_DATA_HOME")) ?? Path.Combine(home, ".local", "share");
        return (config, data);
    }

    /// <summary>Creates the folders that do not exist yet (on Unix, owner-only; existing ones become 0700).</summary>
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
