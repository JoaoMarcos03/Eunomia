using Eunomia.App.Query;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Sql.Completion;
using MySqlConnector;

namespace Eunomia.Integration.Tests;

/// <summary>Autocomplete A sobre a cache de metadata (Fase 6; anexo C §4.2; FAM-Q05).</summary>
public sealed class QueryCompletionTests : IAsyncLifetime
{
    private SessionHost _host = null!;
    private OpenSession _session = null!;
    private string _database = string.Empty;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (TestServer.ConnectionString is null)
        {
            return;
        }
        var password = new MySqlConnectionStringBuilder(TestServer.ConnectionString).Password;
        _host = new SessionHost(new SessionConnectionSettings("it", TestServer.Options()) { KeepAlive = null }, new SessionServices
        {
            Provider = new MySqlProvider(),
            ResolveSecret = (_, _) => ValueTask.FromResult(password),
        });
        await _host.ConnectAsync(Ct);
        _session = new OpenSession(new SessionDefinition { Name = "it" }, _host);
        _database = "eunomia_ac_" + Guid.NewGuid().ToString("N")[..8];
        await _host.RunPrimaryAsync(async (h, ct) =>
        {
            await h.ExecuteNonQueryAsync($"CREATE DATABASE `{_database}`", ct);
            await h.ExecuteNonQueryAsync($"CREATE TABLE `{_database}`.people (id INT PRIMARY KEY, name VARCHAR(20) COMMENT 'nome', `select` INT)", ct);
            await h.ExecuteNonQueryAsync($"CREATE TABLE `{_database}`.orders (id INT, person INT)", ct);
            await h.ExecuteNonQueryAsync($"CREATE VIEW `{_database}`.v AS SELECT id FROM `{_database}`.people", ct);
            await h.ExecuteNonQueryAsync($"CREATE PROCEDURE `{_database}`.p() SELECT 1", ct);
            await h.ExecuteNonQueryAsync($"USE `{_database}`", ct);
            return true;
        }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync($"DROP DATABASE IF EXISTS `{_database}`", ct), CancellationToken.None);
            await _host.DisposeAsync();
        }
    }

    private async Task<CompletionProposal?> At(string textWithCaret, DbObjectRef? selected = null)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        return await new QueryCompletion(_session).ProposeAsync(textWithCaret.Remove(caret, 1), caret, ";", selected, Ct);
    }

    [Fact]
    public async Task AliasesTabelasQualificadasEBases()
    {
        TestServer.RequireServer();
        Assert.Equal(_database, _host.CurrentDatabase);

        // alias. → colunas pela ordem da tabela, com chave, tipo em minúsculas, comentário e crases quando é preciso.
        var alias = await At("SELECT p.| FROM people p JOIN orders o ON o.person = p.id");
        Assert.NotNull(alias);
        Assert.Equal(["id", "name", "select"], alias.Items.Select(i => i.Text));
        Assert.True(alias.Items[0].IsKey);
        Assert.Equal("varchar(20)", alias.Items[1].Detail);
        Assert.Equal("nome", alias.Items[1].Description);
        Assert.Equal("`select`", alias.Items[2].InsertText);
        Assert.Equal(["id", "person"], (await At("SELECT o.| FROM people p JOIN orders o ON o.person = p.id"))!.Items.Select(i => i.Text));

        // db.tbl. → colunas; db. → tabelas, views e rotinas; tbl. da base atual.
        Assert.Equal(["id", "person"], (await At($"SELECT {_database}.orders.|"))!.Items.Select(i => i.Text));
        var objects = (await At($"SELECT * FROM {_database}.|"))!.Items;
        Assert.Equal(["orders", "p", "people", "v"], objects.Select(i => i.Text).Order(StringComparer.Ordinal));
        Assert.Contains(objects, i => i is { Text: "v", Kind: CompletionKind.View });
        Assert.Contains(objects, i => i is { Text: "p", Kind: CompletionKind.Routine, Detail: "procedure" });
        Assert.Equal(["id", "person"], (await At("SELECT orders.|"))!.Items.Select(i => i.Text));

        // Dentro de strings: nada.
        Assert.Null(await At("SELECT 'p.|'"));
    }

    [Fact]
    public async Task SemQualificadorEVariaveis()
    {
        TestServer.RequireServer();
        var people = new DbObjectRef(_database, _database, "people", DbObjectKind.Table);

        // Colunas da tabela selecionada, depois bases, objetos da base atual, funções e keywords.
        var general = (await At("SELECT |", people))!.Items.ToList();
        Assert.Equal(["id", "name", "select"], general.Take(3).Select(i => i.Text));
        int First(CompletionKind kind) => general.FindIndex(i => i.Kind == kind);
        Assert.Equal(0, First(CompletionKind.Column));
        Assert.True(First(CompletionKind.Column) < First(CompletionKind.Database));
        Assert.True(First(CompletionKind.Database) < First(CompletionKind.Table));
        Assert.True(First(CompletionKind.Table) < First(CompletionKind.Function));
        Assert.True(First(CompletionKind.Function) < First(CompletionKind.Keyword));
        Assert.True(First(CompletionKind.Keyword) < First(CompletionKind.DataType));
        Assert.Contains(general, i => i is { Text: var t, Kind: CompletionKind.Database } && t == _database);

        // @@SESSION. com o valor.
        var variables = (await At("SELECT @@SESSION.|"))!.Items;
        Assert.Contains(variables, i => i is { Text: "autocommit", Kind: CompletionKind.Variable, Detail: "ON" });
    }
}
