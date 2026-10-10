using Eunomia.App.Sessions;
using Eunomia.App.Settings;

namespace Eunomia.Infra.Settings.Tests;

/// <summary>Robust <c>sessions.json</c> (phase 14): backup copy, corrupted file and two instances saving.</summary>
public sealed class SessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eunomia-sessions-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string SessionsPath => Path.Combine(_dir, "sessions.json");

    private static SessionDefinition Session(string name, string host = "db.local") => new() { Name = name, Host = host };

    [Fact]
    public void DuasInstanciasNaoApagamOTrabalhoUmaDaOutra()
    {
        var a = new JsonSessionStore(SessionsPath);
        var b = new JsonSessionStore(SessionsPath);
        var shared = Session("prod");
        var seed = new SessionCatalog { Sessions = [shared] };
        a.Save(seed);

        var catalogA = a.Load();
        var catalogB = b.Load();

        // A: a new session and a changed port on the shared one. B: a changed host on the shared one and another new session.
        catalogA.Sessions.Add(Session("dev-a"));
        catalogA.Sessions[0].Port = 3307;
        a.Save(catalogA);
        catalogB.Sessions[0].Host = "db.example.com";
        catalogB.Sessions.Add(Session("dev-b"));
        b.Save(catalogB);

        var final = new JsonSessionStore(SessionsPath).Load();
        Assert.Equal(["dev-a", "dev-b", "prod"], final.Sessions.Select(s => s.Name).Order(StringComparer.Ordinal));
        var prod = final.Sessions.Single(s => s.Name == "prod");
        Assert.Equal(("db.example.com", 3307), (prod.Host, prod.Port));
        // B's in-memory catalog now also has what A saved.
        Assert.Contains(catalogB.Sessions, s => s.Name == "dev-a");
    }

    [Fact]
    public void SessaoApagadaNumaInstanciaNaoVoltaPelaOutra()
    {
        var a = new JsonSessionStore(SessionsPath);
        var b = new JsonSessionStore(SessionsPath);
        a.Save(new SessionCatalog { Sessions = [Session("old"), Session("keep")] });
        var catalogA = a.Load();
        var catalogB = b.Load();

        catalogA.Sessions.RemoveAll(s => s.Name == "old");
        a.Save(catalogA);
        catalogB.Sessions.Single(s => s.Name == "keep").Comment = "nota";
        b.Save(catalogB);

        var final = new JsonSessionStore(SessionsPath).Load();
        Assert.Equal(["keep"], final.Sessions.Select(s => s.Name));
        Assert.Equal("nota", final.Sessions[0].Comment);
    }

    [Fact]
    public void FicheiroCorrompidoVoltaDaCopiaDeSeguranca()
    {
        var store = new JsonSessionStore(SessionsPath);
        store.Save(new SessionCatalog { Sessions = [Session("um")] });
        store.Save(new SessionCatalog { Sessions = [Session("um"), Session("dois")] });
        Assert.True(File.Exists(SessionsPath + ".bak"));

        File.WriteAllText(SessionsPath, "{ \"sessions\": [ { \"name\": ");
        var reopened = new JsonSessionStore(SessionsPath);
        var catalog = reopened.Load();

        Assert.True(reopened.RestoredFromBackup);
        Assert.Equal(["um"], catalog.Sessions.Select(s => s.Name));
        Assert.True(File.Exists(reopened.LastCorruptBackup));
    }

    [Fact]
    public void SettingsCorrompidasVoltamDaCopiaDeSeguranca()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new JsonSettingsStore(path);
        store.Save(new AppSettings { Theme = ThemeNames.Dark });
        store.Save(new AppSettings { Theme = ThemeNames.Dark });
        File.WriteAllText(path, "{ broken");

        Assert.Equal(ThemeNames.Dark, new JsonSettingsStore(path).Load().Theme);
    }

    [Fact]
    public void AppDbDanificadoFicaDeLadoEAAplicacaoArranca()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "app.db");
        File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)0x5A, 8192)]);

        using var db = new SqliteAppDatabase(path);

        Assert.NotNull(db.CorruptBackup);
        Assert.True(File.Exists(db.CorruptBackup));
        Assert.Equal(SqliteAppDatabase.SchemaVersion, db.CurrentSchemaVersion);
        db.SetFavorite("s", "db|t", favorite: true);
        Assert.Contains("db|t", db.GetFavorites("s"));
    }

    [Fact]
    public void EscritaNaoDeixaTemporariosEEmUnixFicaSoParaODono()
    {
        var store = new JsonSessionStore(SessionsPath);
        store.Save(new SessionCatalog { Sessions = [Session("x")] });
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SessionsPath));
        }
    }

    [Fact]
    public void PastasDoSistemaPorPlataforma()
    {
        var env = new Dictionary<string, string?> { ["HOME"] = "/home/ana", ["XDG_CONFIG_HOME"] = "/cfg", ["XDG_DATA_HOME"] = "relativo/ignorado" };
        var (roaming, local) = AppDataPaths.PlatformFolders(k => env.GetValueOrDefault(k));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("/cfg", roaming);
            Assert.Equal(Path.Combine("/home/ana", ".local", "share"), local);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(Path.Combine("/home/ana", "Library", "Application Support"), roaming);
            Assert.Equal(roaming, local);
        }
        else
        {
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), roaming);
        }
    }
}
