using System.Diagnostics;
using Eunomia.App.Query;
using Eunomia.Infra.Settings.Journal;

namespace Eunomia.Infra.Settings.Tests;

/// <summary>Journal das query tabs (TR-31; PR-21; FAM-Q07).</summary>
public sealed class TabJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eunomia-tj-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static TabState State(Guid id, int order = 0, string caption = "Query") => new() { Id = id, Order = order, Caption = caption };

    [Fact]
    public void AlteracoesSobrevivemAoReabrir()
    {
        var id = Guid.NewGuid();
        var journal = new TabJournal(_dir);
        journal.SaveState(State(id) with { PinnedSession = Guid.Empty, PinnedName = "prod", CaretOffset = 4 });
        journal.Append(id, new TextEdit(0, 0, "SELECT 1"));
        journal.Append(id, new TextEdit(7, 1, "42"));
        journal.Append(id, new TextEdit(0, 0, "-- olá\n"));
        journal.Flush();

        var restored = Assert.Single(new TabJournal(_dir).Restore());
        Assert.Equal("-- olá\nSELECT 42", restored.Text);
        Assert.Equal("prod", restored.State.PinnedName);
        Assert.Equal(4, restored.State.CaretOffset);
    }

    [Fact]
    public void RegistoCortadoPorUmCrashEIgnorado()
    {
        var id = Guid.NewGuid();
        var journal = new TabJournal(_dir);
        journal.SaveState(State(id));
        journal.Append(id, new TextEdit(0, 0, "SELECT 1;"));
        journal.Flush();
        journal.Append(id, new TextEdit(9, 0, " SELECT 2;"));
        journal.Flush();

        // Kill a meio da escrita do registo seguinte: só metade chegou ao disco.
        var path = Directory.GetFiles(_dir, "*.journal").Single();
        var full = File.ReadAllBytes(path);
        journal.Append(id, new TextEdit(19, 0, " SELECT 3;"));
        journal.Flush();
        var withThird = File.ReadAllBytes(path);
        File.WriteAllBytes(path, withThird[..(full.Length + ((withThird.Length - full.Length) / 2))]);
        Assert.Equal("SELECT 1; SELECT 2;", TabJournal.Replay(path));

        // Um byte trocado falha o CRC: fica o que estava antes.
        withThird[full.Length + 10] ^= 0xFF;
        File.WriteAllBytes(path, withThird);
        Assert.Equal("SELECT 1; SELECT 2;", TabJournal.Replay(path));
    }

    [Fact]
    public void ResetCompactacaoERemove()
    {
        var id = Guid.NewGuid();
        var journal = new TabJournal(_dir);
        journal.SaveState(State(id));
        journal.Append(id, new TextEdit(0, 0, "antigo"));
        journal.Reset(id, "novo");
        journal.Append(id, new TextEdit(4, 0, "!"));
        journal.Flush();
        var path = Directory.GetFiles(_dir, "*.journal").Single();
        Assert.Equal("novo!", TabJournal.Replay(path));

        for (var i = 0; i < TabJournal.CompactAfterEdits; i++)
        {
            journal.Append(id, new TextEdit(5, 0, "x"));
        }
        journal.Flush();
        Assert.Equal("novo!" + new string('x', TabJournal.CompactAfterEdits), TabJournal.Replay(path));
        Assert.True(new FileInfo(path).Length < 100 + TabJournal.CompactAfterEdits, "Compactado num snapshot.");

        journal.Remove(id);
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Empty(new TabJournal(_dir).Restore());
    }

    [Fact]
    public void OrdemEStatesDeOutraInstanciaFicamDeFora()
    {
        var journal = new TabJournal(_dir);
        var second = Guid.NewGuid();
        var first = Guid.NewGuid();
        journal.SaveState(State(second, order: 1, caption: "Query #2"));
        journal.SaveState(State(first, order: 0));
        Assert.Equal([first, second], new TabJournal(_dir).Restore().Select(t => t.State.Id));

        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // Uma tab de outra instância viva (outro processo) não é restaurada.
        using var other = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var foreign = Guid.NewGuid();
            var json = $$"""{"state":{"id":"{{foreign}}","order":2,"caption":"x"},"pid":{{other.Id}},"processStart":{{other.StartTime.ToUniversalTime().Ticks}}}""";
            File.WriteAllText(Path.Combine(_dir, $"query-tab-{foreign:N}.json"), json);
            Assert.DoesNotContain(new TabJournal(_dir).Restore(), t => t.State.Id == foreign);
        }
        finally
        {
            other.Kill(entireProcessTree: true);
        }
    }
}
