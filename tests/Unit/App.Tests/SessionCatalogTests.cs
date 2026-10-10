using Eunomia.App.Sessions;

namespace Eunomia.App.Tests;

/// <summary>Regras do Session Manager do HeidiSQL (FAM-S03; anexo A §1.3–1.5).</summary>
public sealed class SessionCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static SessionCatalog Sample()
    {
        var c = new SessionCatalog();
        c.Sessions.Add(new SessionDefinition { Name = "localhost" });
        c.Sessions.Add(new SessionDefinition { Name = "StagingAmberline", Folder = ["Amberline"], Host = "staging.example.com" });
        c.Sessions.Add(new SessionDefinition { Name = "PROD_Amberline", Folder = ["Amberline"], Host = "prod.example.com", Color = 0xDC143C });
        c.Sessions.Add(new SessionDefinition { Name = "deep", Folder = ["Elmwood", "Old"] });
        c.Folders.Add(["Sycamore"]);
        return c;
    }

    [Fact]
    public void NovosNomesUnnamedSequenciais()
    {
        var c = new SessionCatalog();

        Assert.Equal("Unnamed", c.CreateSession([], Now).Name);
        Assert.Equal("Unnamed-1", c.CreateSession([], Now).Name);
        Assert.Equal(["Unnamed-2"], c.CreateFolder([]));
        Assert.Equal("Unnamed", c.CreateSession(["Unnamed-2"], Now).Name);
        Assert.Equal(Now, c.Sessions[0].Statistics.Created);
    }

    [Fact]
    public void FoldersImplicitosEExplicitos()
    {
        var folders = Sample().AllFolders().Select(f => string.Join('\\', f)).ToList();

        Assert.Equal(["Sycamore", "Amberline", "Elmwood", "Elmwood\\Old"], folders);
    }

    [Fact]
    public void ArvoreComFoldersNoTopoEOrdenada()
    {
        var tree = Sample().BuildTree();

        Assert.Equal(["Amberline", "Elmwood", "Sycamore", "localhost"], tree.Select(n => n.Name));
        Assert.Equal(["PROD_Amberline", "StagingAmberline"], tree[0].Children.Select(n => n.Name));
        Assert.True(tree[1].Children[0].IsFolder);
        Assert.Equal("deep", tree[1].Children[0].Children[0].Name);

        var mixed = Sample().BuildTree(foldersAtTop: false);
        Assert.Equal(["Amberline", "Elmwood", "localhost", "Sycamore"], mixed.Select(n => n.Name));
    }

    [Fact]
    public void RenameTrocaBarraPorHifenERecusaRepetidosEMaiusculas()
    {
        var c = Sample();
        var staging = c.FindByPath("Amberline\\StagingAmberline")!;

        Assert.Null(c.RenameSession(staging, "Stag\\ing"));
        Assert.Equal("Stag-ing", staging.Name);
        Assert.Equal("Session \"prod_amberline\" already exists!", c.RenameSession(staging, "prod_amberline"));
        Assert.Contains("letter case", c.RenameSession(staging, "STAG-ING"), StringComparison.Ordinal);
        Assert.Equal("Stag-ing", staging.Name);
    }

    [Fact]
    public void RenameDeFolderMudaOCaminhoDasSessoes()
    {
        var c = Sample();

        Assert.Null(c.RenameFolder(["Elmwood"], "Eco"));

        Assert.Equal("Eco\\Old\\deep", c.Sessions[3].Path);
        Assert.False(c.FolderExists(["Elmwood"]));
        Assert.True(c.FolderExists(["Eco", "Old"]));
    }

    [Fact]
    public void DragAndDropComAsRegrasDoHeidiSql()
    {
        var c = Sample();
        var local = c.FindByPath("localhost")!;

        Assert.Null(c.MoveSession(local, ["Amberline"]));
        Assert.Equal("Amberline\\localhost", local.Path);
        Assert.NotNull(c.MoveSession(local, ["Amberline"]));

        c.Sessions.Add(new SessionDefinition { Name = "localhost" });
        Assert.Equal("Session \"localhost\" already exists!", c.MoveSession(c.Sessions[^1], ["Amberline"]));

        Assert.NotNull(c.MoveFolder(["Elmwood"], ["Elmwood", "Old"]));
        Assert.NotNull(c.MoveFolder(["Elmwood", "Old"], ["Elmwood"]));
        Assert.Null(c.MoveFolder(["Elmwood", "Old"], ["Sycamore"]));
        Assert.Equal("Sycamore\\Old\\deep", c.Sessions[3].Path);
    }

    [Fact]
    public void ApagarFolderApagaAsSessoesLaDentro()
    {
        var c = Sample();

        var removed = c.DeleteFolder(["Amberline"]);

        Assert.Equal(2, removed.Count);
        Assert.Equal(["localhost", "deep"], c.Sessions.Select(s => s.Name));
        Assert.False(c.FolderExists(["Amberline"]));
    }

    [Fact]
    public void DuplicateCriaOutroIdNoMesmoFolder()
    {
        var c = Sample();
        var prod = c.FindByPath("Amberline\\PROD_Amberline")!;
        prod.PasswordSaved = true;
        prod.Statistics.ConnectCount = 55;

        var copy = c.Duplicate(prod, "PROD_Amberline_2", Now);

        Assert.NotEqual(prod.Id, copy.Id);
        Assert.Equal("Amberline\\PROD_Amberline_2", copy.Path);
        Assert.Equal(0xDC143Cu, copy.Color);
        Assert.False(copy.PasswordSaved);
        Assert.Equal(0, copy.Statistics.ConnectCount);
        Assert.Throws<InvalidOperationException>(() => c.Duplicate(prod, "stagingamberline", Now));
    }

    [Theory]
    [InlineData("prod", new[] { "PROD_Amberline" })]
    [InlineData("example\\.com$", new[] { "StagingAmberline", "PROD_Amberline" })]
    [InlineData("^loc", new[] { "localhost" })]
    [InlineData("3306", new[] { "localhost", "StagingAmberline", "PROD_Amberline", "deep" })]
    [InlineData("[invalid", new[] { "localhost", "StagingAmberline", "PROD_Amberline", "deep" })]
    public void FiltroRegexEmTodasAsColunas(string filter, string[] expected)
    {
        var c = Sample();
        var match = SessionCatalog.CreateFilter(filter);

        Assert.Equal(expected, c.Sessions.Where(match).Select(s => s.Name));
    }

    [Fact]
    public void MudarTipoDeRedeRepoeDefaultsNaoMexidos()
    {
        var s = new SessionDefinition();
        s.ChangeNetType(NetType.MySqlSshTunnel);
        Assert.True(s.Ssh.Active);
        Assert.Equal(3306, s.Port);

        s.Port = 3307;
        s.ChangeNetType(NetType.PgSqlTcpIp);
        Assert.Equal(3307, s.Port);
        Assert.Equal("postgres", s.User);
        Assert.False(s.Ssh.Active);
    }
}
