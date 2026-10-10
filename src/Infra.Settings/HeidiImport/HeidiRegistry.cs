using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Eunomia.Infra.Settings.HeidiImport;

/// <summary>
/// Settings do HeidiSQL instalado neste computador (Fase 13): o HeidiSQL guarda-as em <c>HKCU\Software\HeidiSQL</c>, com a
/// mesma árvore que o ficheiro exportado (subchaves = caminho, valores de texto = tipo 1, DWORD = tipo 3). Ler o
/// registo dá o mesmo <see cref="HeidiSettingsFile"/> que o import do ficheiro, sem o utilizador ter de exportar
/// primeiro.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HeidiRegistry
{
    public const string DefaultKey = @"Software\HeidiSQL";

    /// <summary>Há pelo menos uma sessão do HeidiSQL (subchave de <c>Servers</c>) neste utilizador.</summary>
    public static bool HasSessions(string keyPath = DefaultKey)
    {
        using var servers = Registry.CurrentUser.OpenSubKey(keyPath + @"\Servers");
        return servers is not null && servers.SubKeyCount > 0;
    }

    /// <summary>Os valores da chave do HeidiSQL, ou <c>null</c> se não existir.</summary>
    public static HeidiSettingsFile? Read(string keyPath = DefaultKey)
    {
        using var root = Registry.CurrentUser.OpenSubKey(keyPath);
        if (root is null)
        {
            return null;
        }
        var file = new HeidiSettingsFile();
        Walk(root, [], file);
        return file;
    }

    private static void Walk(RegistryKey key, List<string> path, HeidiSettingsFile file)
    {
        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            switch (key.GetValueKind(name))
            {
                case RegistryValueKind.String or RegistryValueKind.ExpandString when value is string text:
                    file.Values.Add(new HeidiValue([.. path], name, 1, text));
                    break;
                case RegistryValueKind.DWord when value is int number:
                    file.Values.Add(new HeidiValue([.. path], name, 3, number.ToString(CultureInfo.InvariantCulture)));
                    break;
                case RegistryValueKind.QWord when value is long number:
                    file.Values.Add(new HeidiValue([.. path], name, 3, number.ToString(CultureInfo.InvariantCulture)));
                    break;
            }
        }
        foreach (var child in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(child);
            if (sub is not null)
            {
                Walk(sub, [.. path, child], file);
            }
        }
    }
}
