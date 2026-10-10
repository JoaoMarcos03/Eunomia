using System.Text.Json;
using System.Text.Json.Serialization;
using Eunomia.App.Settings;

namespace Eunomia.Infra.Settings;

/// <summary>
/// Settings globais em JSON (secção 21.4). Lê com tolerância: ficheiro ausente dá os defaults; ficheiro corrompido
/// é guardado ao lado com o sufixo <c>.corrupt-&lt;data&gt;</c> e as settings voltam da versão anterior
/// (<c>.bak</c>, gravada em cada Save) ou, sem ela, dos defaults, para a aplicação arrancar sempre. Campos desconhecidos
/// (de uma versão mais nova) são ignorados.
/// </summary>
public sealed class JsonSettingsStore(string path, TimeProvider? clock = null) : ISettingsStore
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Path { get; } = path;

    /// <summary>Último ficheiro corrompido posto de lado por <see cref="Load"/>, para avisar o utilizador.</summary>
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
            // A versão anterior (gravada ao lado em cada Save) evita perder as preferências todas.
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

/// <summary>Passos de migração entre versões do formato (vazio enquanto só existe a versão 1).</summary>
public static class SettingsMigrations
{
    public static AppSettings Upgrade(AppSettings settings)
    {
        // Versões futuras: if (settings.Version < 2) { ...; settings.Version = 2; }
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

/// <summary>Serialização gerada em compile time (segura para NativeAOT e trimming).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
