using System.Globalization;
using Eunomia.App.Commands;
using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;
using Eunomia.Infra.Settings.HeidiImport;
using Microsoft.Win32;

namespace Eunomia.Infra.Settings.Tests;

/// <summary>HeidiSQL settings read from the registry (phase 13): the same tree as the exported file.</summary>
public sealed class HeidiRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadsTheRegistrySettingsLikeTheFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Registry only on Windows.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "heidisql-export-sample.txt"))
            .Replace("PASSWORD_ANSI", HeidiPassword.Encrypt("root-pw", 4), StringComparison.Ordinal)
            .Replace("PASSWORD_UNICODE", HeidiPassword.Encrypt("ssh-pw-€", 7), StringComparison.Ordinal);
        var key = @"Software\EunomiaTests\" + Guid.NewGuid().ToString("N");
        try
        {
            // The same tree HeidiSQL writes: subkeys by path, text as REG_SZ and integers as DWORD.
            foreach (var value in HeidiSettingsFile.Parse(text).Values)
            {
                using var target = Registry.CurrentUser.CreateSubKey(string.Join('\\', [key, .. value.KeyPath]));
                if (value.Type == 3 && int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    target.SetValue(value.Name, number, RegistryValueKind.DWord);
                }
                else
                {
                    target.SetValue(value.Name, value.Value, RegistryValueKind.String);
                }
            }

            Assert.True(HeidiRegistry.HasSessions(key));
            var file = HeidiRegistry.Read(key)!;
            var catalog = new SessionCatalog();
            var secrets = new MemorySecretStore();
            new HeidiSettingsImporter(catalog, new AppSettings(), HeidiSqlCatalog.CreateRegistry(), secrets, null).Import(file, Now);

            var local = catalog.FindByPath("localhost")!;
            Assert.Equal(0x006400u, local.Color);
            Assert.Equal("Line 1\r\nLine 2", local.Comment);
            Assert.Equal("root-pw", secrets.Get(SecretKeys.Password(local.Id)));
            Assert.Equal(3307, catalog.FindByPath(@"Amberline\StagingAmberline")!.Port);
            Assert.False(HeidiRegistry.HasSessions(key + @"\Nothing"));
            Assert.Null(HeidiRegistry.Read(key + @"\Nothing"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }
}
