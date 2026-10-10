using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Eunomia.Infra.Settings.HeidiImport;

/// <summary>
/// Settings of the HeidiSQL installed on this computer (phase 13): HeidiSQL keeps them in <c>HKCU\Software\HeidiSQL</c>, with
/// the same tree as the exported file (subkeys = path, text values = type 1, DWORD = type 3). Reading the
/// registry gives the same <see cref="HeidiSettingsFile"/> as the file import, without the user having to export
/// first.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HeidiRegistry
{
    public const string DefaultKey = @"Software\HeidiSQL";

    /// <summary>There is at least one HeidiSQL session (a subkey of <c>Servers</c>) for this user.</summary>
    public static bool HasSessions(string keyPath = DefaultKey)
    {
        using var servers = Registry.CurrentUser.OpenSubKey(keyPath + @"\Servers");
        return servers is not null && servers.SubKeyCount > 0;
    }

    /// <summary>The values of the HeidiSQL key, or <c>null</c> if it does not exist.</summary>
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
