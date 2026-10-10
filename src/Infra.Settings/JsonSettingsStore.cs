using System.Text.Json;
using System.Text.Json.Serialization;
using Eunomia.App.Settings;

namespace Eunomia.Infra.Settings;

/// <summary>
/// Global settings in JSON (section 21.4). Reads tolerantly: a missing file gives the defaults; a corrupted file
/// is kept next to it with the <c>.corrupt-&lt;date&gt;</c> suffix and the settings come back from the previous version
/// (<c>.bak</c>, written on every Save) or, without it, from the defaults, so the application always starts. Unknown fields
/// (from a newer version) are ignored.
/// </summary>
public sealed class JsonSettingsStore(string path, TimeProvider? clock = null) : ISettingsStore
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Path { get; } = path;

    /// <summary>Last corrupted file set aside by <see cref="Load"/>, to warn the user.</summary>
    public string? LastCorruptBackup { get; private set; }

    public AppSettings Load()
    {
        if (!File.Exists(Path))
        {
            return new AppSettings();
        }
        try
        {
            var bytes = File.ReadAllBytes(Path);
            var settings = JsonSerializer.Deserialize(bytes, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            return SettingsMigrations.Upgrade(settings);
        }
        catch (JsonException)
        {
            var backup = $"{Path}.corrupt-{_clock.GetUtcNow():yyyyMMddHHmmss}";
            File.Move(Path, backup, overwrite: true);
            LastCorruptBackup = backup;
            // The previous version (written next to it on every Save) avoids losing all the preferences.
            try
            {
                if (File.Exists(Path + ".bak") && JsonSerializer.Deserialize(File.ReadAllBytes(Path + ".bak"), SettingsJsonContext.Default.AppSettings) is { } previous)
                {
                    return SettingsMigrations.Upgrade(previous);
                }
            }
            catch (JsonException)
            {
            }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Version = AppSettings.CurrentVersion;
        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        AtomicFile.WriteAllText(Path, json, keepBackup: true);
    }
}

/// <summary>Migration steps between format versions (empty while only version 1 exists).</summary>
public static class SettingsMigrations
{
    public static AppSettings Upgrade(AppSettings settings)
    {
        // Future versions: if (settings.Version < 2) { ...; settings.Version = 2; }
        settings.Shortcuts ??= [];
        settings.MainWindow ??= new WindowGeometry();
        settings.Panels ??= new PanelLayout();
        settings.Display ??= new DisplayOptions();
        settings.Log ??= new LogOptions();
        settings.Tabs ??= new TabOptions();
        settings.Query ??= new QueryOptions();
        settings.Data ??= new DataOptions();
        settings.SessionManager ??= new SessionManagerOptions();
        settings.Search ??= new SearchSettings();
        settings.SqlExport ??= new SqlExportSettings();
        settings.CsvImport ??= new CsvImportSettings();
        settings.Theme ??= ThemeNames.Light;
        return settings;
    }
}

/// <summary>Serialisation generated at compile time (safe for NativeAOT and trimming).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
