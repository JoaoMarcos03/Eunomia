using Eunomia.App.Sessions;

namespace Eunomia.App.Tests;

/// <summary>HeidiSQL-compatible command line (FAM-S06; appendix A §9).</summary>
public sealed class CommandLineTests
{
    [Fact]
    public void FormasDeEscreverOsValores()
    {
        var r = CommandLine.Parse(["-h=db.example.com", "-P", "3307", "--user=app", "-p\"se cret\"", "-db=shop;crm", "C:\\x\\script.sql", "--theme", "dark"]);

        Assert.Equal("db.example.com", r.Host);
        Assert.Equal(3307, r.Port);
        Assert.Equal("app", r.User);
        Assert.Equal("se cret", r.Password);
        Assert.Equal("shop;crm", r.Databases);
        Assert.Equal(["C:\\x\\script.sql"], r.SqlFiles);
        Assert.True(r.HasConnectionOverrides);
    }

    [Fact]
    public void ValorNaoPodeComecarPorHifen()
    {
        var r = CommandLine.Parse(["-u", "-p=x"]);

        Assert.Null(r.User);
        Assert.Equal("x", r.Password);
    }

    [Fact]
    public void SessaoGravadaComParametrosPorCimaEhAdHoc()
    {
        var catalog = new SessionCatalog();
        var saved = new SessionDefinition { Name = "prod", Folder = ["Clients"], Host = "prod", Port = 3306, User = "root", Color = 0xDC143C };
        catalog.Sessions.Add(saved);

        var plain = CommandLine.Resolve(CommandLine.Parse(["-d=Clients\\prod"]), catalog);
        var adHoc = CommandLine.Resolve(CommandLine.Parse(["-d=Clients\\prod", "-u=reader"]), catalog)!;

        Assert.Same(saved, plain);
        Assert.NotEqual(saved.Id, adHoc.Id);
        Assert.Equal("reader", adHoc.User);
        Assert.Equal(0xDC143Cu, adHoc.Color);
        Assert.StartsWith("prod-", adHoc.Name, StringComparison.Ordinal);
        Assert.Empty(adHoc.Folder);
        Assert.Single(catalog.Sessions);
    }

    [Fact]
    public void SshPelaLinhaDeComandos()
    {
        var s = CommandLine.Resolve(CommandLine.Parse(["-h=10.0.0.5", "-sh=bastion", "-su=deploy", "-sP=2222"]), new SessionCatalog())!;

        Assert.True(s.Ssh.Active);
        Assert.Equal(("bastion", 2222, "deploy"), (s.Ssh.Host, s.Ssh.Port, s.Ssh.User));
    }

    [Fact]
    public void SemNadaEhVazio()
    {
        Assert.True(CommandLine.Parse(["--theme", "dark", "--startup-probe", "x.json"]).IsEmpty);
    }
}
