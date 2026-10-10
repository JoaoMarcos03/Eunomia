using Eunomia.App.Commands;
using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;
using Eunomia.Infra.Settings.HeidiImport;

namespace Eunomia.Infra.Settings.Tests;

/// <summary>
/// Import of the HeidiSQL settings file (FAM-S05). The sample file follows the format of appendix A §1.8;
/// the comparison with a real HeidiSQL 12.21 export is still pending (phase 0 golden file, a manual check).
/// </summary>
public sealed class HeidiImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("6465663", "abc")]
    [InlineData("049", "ú")]
    [InlineData("20AE20", "€")]
    [InlineData("", "")]
    public void DesofuscaPasswordsComoOHeidiSql(string encoded, string expected)
    {
        Assert.Equal(expected, HeidiPassword.Decrypt(encoded));
    }

    [Theory]
    [InlineData("s3cr3t!", 1)]
    [InlineData("çãõ éúÿ", 9)]
    [InlineData("пароль€", 5)]
    public void EncryptEDecryptSaoInversos(string password, int salt)
    {
        Assert.Equal(password, HeidiPassword.Decrypt(HeidiPassword.Encrypt(password, salt)));
    }

    [Theory]
    [InlineData("12Z")]
    [InlineData("1G5")]
    public void PasswordInvalidaDaErro(string encoded)
    {
        Assert.Throws<FormatException>(() => HeidiPassword.Decrypt(encoded));
    }

    [Theory]
    [InlineData(16397, "Ctrl+Enter")]
    [InlineData(120, "F9")]
    [InlineData(24696, "Ctrl+Shift+F9")]
    [InlineData(49186, "Ctrl+Alt+PgDn")]
    [InlineData(32776, "Alt+Backspace")]
    [InlineData(16433, "Ctrl+1")]
    public void DescodificaTShortCut(int code, string expected)
    {
        Assert.Equal(KeyChord.Parse(expected), HeidiSettingsImporter.DecodeShortcut(code));
    }

    [Theory]
    [InlineData(25600, 0x006400u)]
    [InlineData(6063222, 0x76845Cu)]
    [InlineData(0x003C14DC, 0xDC143Cu)]
    public void CoresTColorParaRgb(int tcolor, uint rgb)
    {
        Assert.Equal(rgb, HeidiSettingsImporter.ToColor(tcolor));
    }

    [Theory]
    [InlineData(536870911)]
    [InlineData(0x20000000)]
    [InlineData(-16777201)]
    public void SemCor(int tcolor)
    {
        Assert.Null(HeidiSettingsImporter.ToColor(tcolor));
    }

    private static (HeidiImportResult Result, SessionCatalog Catalog, AppSettings Settings, MemorySecretStore Secrets) ImportSample(SessionCatalog? existing = null)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "heidisql-export-sample.txt"))
            .Replace("PASSWORD_ANSI", HeidiPassword.Encrypt("root-pw", 4), StringComparison.Ordinal)
            .Replace("PASSWORD_UNICODE", HeidiPassword.Encrypt("ssh-pw-€", 7), StringComparison.Ordinal);
        var catalog = existing ?? new SessionCatalog();
        var settings = new AppSettings();
        var secrets = new MemorySecretStore();
        var importer = new HeidiSettingsImporter(catalog, settings, HeidiSqlCatalog.CreateRegistry(), secrets, null);
        return (importer.Import(HeidiSettingsFile.Parse(text), Now), catalog, settings, secrets);
    }

    [Fact]
    public void ImportaSessoesFoldersCoresSshESsl()
    {
        var (result, catalog, _, secrets) = ImportSample();

        Assert.Equal(["localhost", "Amberline\\StagingAmberline", "Amberline\\Old\\PROD_Amberline"], catalog.Sessions.Select(s => s.Path).Order(StringComparer.Ordinal).Reverse().OrderBy(p => p.Count(c => c == '\\')));
        Assert.Equal(3, result.ImportedFolders);
        Assert.True(catalog.FolderExists(["Empty"]));

        var local = catalog.FindByPath("localhost")!;
        Assert.Equal(0x006400u, local.Color);
        Assert.Equal(63, local.Statistics.ConnectCount);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 9, 30, 18, 22, 5, DateTimeKind.Local)), local.Statistics.LastConnect);
        Assert.Equal("Linha 1\r\nLinha 2", local.Comment);
        Assert.True(local.PasswordSaved);
        Assert.Equal("root-pw", secrets.Get(SecretKeys.Password(local.Id)));

        var staging = catalog.FindByPath("Amberline\\StagingAmberline")!;
        Assert.Equal(NetType.MySqlSshTunnel, staging.NetType);
        Assert.Equal(3307, staging.Port);
        Assert.Equal(0x76845Cu, staging.Color);
        Assert.True(staging.Ssh.Active);
        Assert.Equal(("bastion.example.com", 2222, "deploy"), (staging.Ssh.Host, staging.Ssh.Port, staging.Ssh.User));
        Assert.Equal("C:\\keys\\id_ed25519", staging.Ssh.PrivateKeyFile);
        Assert.Equal(1, staging.Ssh.Timeout);
        Assert.Equal("ssh-pw-€", secrets.Get(SecretKeys.SshPassword(staging.Id)));
        Assert.True(staging.Ssl.Active);
        Assert.Equal("C:\\certs\\ca.pem", staging.Ssl.CaCertificate);
        Assert.Equal(1, staging.Ssl.Verification);
        Assert.Equal("elmwood;shop", staging.Databases);
        Assert.Equal("^tmp_", staging.HideDatabasePattern);
        Assert.True(staging.PasswordSaved);
        Assert.Equal(string.Empty, secrets.Get(SecretKeys.Password(staging.Id)));

        var prod = catalog.FindByPath("Amberline\\Old\\PROD_Amberline")!;
        Assert.Equal(NetType.MySqlTcpIp, prod.NetType);
        Assert.Null(prod.Color);
        Assert.Contains(result.Warnings, w => w.Contains("unknown network type 99", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("1 invalid line", StringComparison.Ordinal));
        Assert.Equal(2, result.ImportedPasswords);
    }

    [Fact]
    public void SubchavesDeEstadoPorTabelaNaoSaoSessoes()
    {
        var (_, catalog, _, _) = ImportSample();

        Assert.DoesNotContain(catalog.Sessions, s => s.Name.Contains('|', StringComparison.Ordinal) || s.Name == "QueryHistory");
    }

    [Fact]
    public void ImportaAtalhosDiferentesDoPorOmissao()
    {
        var (result, _, settings, _) = ImportSample();

        Assert.Equal("Ctrl+Enter", settings.Shortcuts["query.run"]);
        Assert.Equal(string.Empty, settings.Shortcuts["file.newWindow"]);
        Assert.False(settings.Shortcuts.ContainsKey("view.refresh"));
        Assert.Equal(2, result.ImportedShortcuts);
        Assert.Contains(result.Warnings, w => w.Contains("actDoesNotExist", StringComparison.Ordinal));
    }

    [Fact]
    public void ImportNaoSubstituiSessoesExistentes()
    {
        var existing = new SessionCatalog();
        existing.Sessions.Add(new SessionDefinition { Name = "localhost", Host = "meu-host" });

        var (result, catalog, _, _) = ImportSample(existing);

        Assert.Equal(["localhost"], result.SkippedSessions);
        Assert.Equal("meu-host", catalog.FindByPath("localhost")!.Host);
        Assert.Equal(2, result.ImportedSessions.Count);
    }
}
