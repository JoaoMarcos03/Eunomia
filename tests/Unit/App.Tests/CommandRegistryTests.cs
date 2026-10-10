using Eunomia.App.Commands;

namespace Eunomia.App.Tests;

public sealed class CommandRegistryTests
{
    private static CommandRegistry Sample()
    {
        var r = new CommandRegistry();
        r.Register(new CommandDescriptor("query.run", "Run", "Query", KeyChord.Parse("F9"), [CommandContexts.QueryEditor], HeidiSqlAction: "actExecuteQuery"));
        r.Register(new CommandDescriptor("filter.apply", "Apply filter", "Data", KeyChord.Parse("F9"), [CommandContexts.FilterPanel]));
        r.Register(new CommandDescriptor("tree.focusTableFilter", "Table filter", "Navigation", KeyChord.Parse("Ctrl+E")));
        r.Register(new CommandDescriptor("tree.focus", "Database tree", "Navigation", KeyChord.Parse("Ctrl+D")));
        r.Register(new CommandDescriptor("file.newQueryTab", "New query tab", "File", KeyChord.Parse("Ctrl+T"), AvailableIn: "0.2"));
        return r;
    }

    [Theory]
    [InlineData("F9", ChordModifiers.None, "F9")]
    [InlineData("ctrl+shift+f9", ChordModifiers.Ctrl | ChordModifiers.Shift, "F9")]
    [InlineData("Shift+Ctrl+F9", ChordModifiers.Ctrl | ChordModifiers.Shift, "F9")]
    [InlineData("Alt+Enter", ChordModifiers.Alt, "Enter")]
    [InlineData("Ctrl+Ins", ChordModifiers.Ctrl, "Ins")]
    [InlineData("Ctrl+Insert", ChordModifiers.Ctrl, "Ins")]
    [InlineData("Ctrl+e", ChordModifiers.Ctrl, "E")]
    [InlineData("Ctrl+Alt+PgDn", ChordModifiers.Ctrl | ChordModifiers.Alt, "PgDn")]
    public void ParsesShortcuts(string text, ChordModifiers modifiers, string key)
    {
        Assert.Equal(new KeyChord(modifiers, key), KeyChord.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Hyper+A")]
    public void RejectsInvalidShortcuts(string text)
    {
        Assert.False(KeyChord.TryParse(text, out _));
    }

    [Fact]
    public void ToStringIsCanonical()
    {
        Assert.Equal("Ctrl+Shift+F9", KeyChord.Parse("shift+ctrl+f9").ToString());
    }

    [Fact]
    public void F9DependsOnTheFocusContext()
    {
        var r = Sample();
        var f9 = KeyChord.Parse("F9");

        Assert.Equal("query.run", r.Resolve(f9, CommandContexts.QueryEditor)?.Id);
        Assert.Equal("filter.apply", r.Resolve(f9, CommandContexts.FilterPanel)?.Id);
        Assert.Null(r.Resolve(f9, CommandContexts.Grid));
    }

    [Fact]
    public void GlobalCommandWorksInAnyContext()
    {
        var r = Sample();

        Assert.Equal("tree.focusTableFilter", r.Resolve(KeyChord.Parse("Ctrl+E"), CommandContexts.Grid)?.Id);
        Assert.Equal("tree.focusTableFilter", r.Resolve(KeyChord.Parse("Ctrl+E"), CommandContexts.QueryEditor)?.Id);
    }

    [Fact]
    public void ContextCommandWinsOverTheGlobalOne()
    {
        var r = Sample();
        r.Register(new CommandDescriptor("editor.something", "Editor Ctrl+E", "Editor", KeyChord.Parse("Ctrl+E"), [CommandContexts.QueryEditor]));

        Assert.Equal("editor.something", r.Resolve(KeyChord.Parse("Ctrl+E"), CommandContexts.QueryEditor)?.Id);
        Assert.Equal("tree.focusTableFilter", r.Resolve(KeyChord.Parse("Ctrl+E"), CommandContexts.Grid)?.Id);
        Assert.Empty(r.FindConflicts());
    }

    [Fact]
    public void ConflictsInTheSameContextOrBetweenGlobals()
    {
        var r = Sample();
        Assert.Empty(r.FindConflicts());

        r.SetShortcut("tree.focus", KeyChord.Parse("Ctrl+E"));
        var conflict = Assert.Single(r.FindConflicts());
        Assert.Equal(("tree.focusTableFilter", "tree.focus", CommandContexts.Global), (conflict.FirstId, conflict.SecondId, conflict.Context));

        r.ResetShortcut("tree.focus");
        Assert.Empty(r.FindConflicts());
        Assert.Equal(KeyChord.Parse("Ctrl+D"), r.ShortcutOf("tree.focus"));
    }

    [Fact]
    public void RemoveShortcut()
    {
        var r = Sample();
        r.SetShortcut("query.run", null);

        Assert.Null(r.ShortcutOf("query.run"));
        Assert.Null(r.Resolve(KeyChord.Parse("F9"), CommandContexts.QueryEditor));
    }

    [Fact]
    public async Task WithoutAHandlerItIsDisabledWithATooltip()
    {
        var r = Sample();

        var state = r.StateOf("file.newQueryTab");
        Assert.False(state.IsImplemented);
        Assert.False(state.CanExecute);
        Assert.Equal("Available in version 0.2", state.Tooltip);
        Assert.False(await r.ExecuteAsync("file.newQueryTab", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WithAHandlerItRunsAndHonoursCanExecute()
    {
        var r = Sample();
        var runs = 0;
        var enabled = false;
        r.SetHandler("query.run", _ => { runs++; return ValueTask.CompletedTask; }, () => enabled);

        Assert.False(await r.ExecuteAsync("query.run", TestContext.Current.CancellationToken));
        enabled = true;
        Assert.True(await r.ExecuteAsync("query.run", TestContext.Current.CancellationToken));
        Assert.Equal(1, runs);
        Assert.Null(r.StateOf("query.run").Tooltip);
    }

    [Fact]
    public void IdsAreUnique()
    {
        var r = Sample();

        Assert.Throws<InvalidOperationException>(() => r.Register(new CommandDescriptor("query.run", "x", "y")));
        Assert.Throws<KeyNotFoundException>(() => r.Get("does.not.exist"));
    }

    [Fact]
    public void RegistrationOrderKeepsTheMenuGeography()
    {
        Assert.Equal(
            ["query.run", "filter.apply", "tree.focusTableFilter", "tree.focus", "file.newQueryTab"],
            Sample().All.Select(c => c.Id));
    }

    [Fact]
    public void DisabledCommandDoesNotConsumeTheShortcut()
    {
        // Esc: Cancel editing (grid, only with pending changes) vs Cancel running operation (global, only while running).
        var r = new CommandRegistry();
        r.Register(new CommandDescriptor("grid.cancelEditing", "Cancel editing", "Data", KeyChord.Parse("Esc"), [CommandContexts.Grid]));
        r.Register(new CommandDescriptor("operation.cancel", "Cancel running operation", "Various", KeyChord.Parse("Esc")));
        var pending = false;
        var running = false;
        r.SetHandler("grid.cancelEditing", () => { }, () => pending);
        r.SetHandler("operation.cancel", () => { }, () => running);
        var esc = KeyChord.Parse("Esc");
        string[] gridFocus = [CommandContexts.Grid];

        Assert.Null(r.ResolveExecutable(esc, gridFocus));
        running = true;
        Assert.Equal("operation.cancel", r.ResolveExecutable(esc, gridFocus)?.Id);
        pending = true;
        Assert.Equal("grid.cancelEditing", r.ResolveExecutable(esc, gridFocus)?.Id);
        Assert.Equal("operation.cancel", r.ResolveExecutable(esc, [CommandContexts.QueryEditor])?.Id);
    }

    [Fact]
    public void FocusContextsFromTheInnermostToTheOutermost()
    {
        var r = Sample();
        r.SetHandler("query.run", () => { });
        r.SetHandler("filter.apply", () => { });

        Assert.Equal("filter.apply", r.ResolveExecutable(KeyChord.Parse("F9"), [CommandContexts.FilterPanel, CommandContexts.QueryEditor])?.Id);
        Assert.Equal("query.run", r.ResolveExecutable(KeyChord.Parse("F9"), [CommandContexts.Grid, CommandContexts.QueryEditor])?.Id);
    }

    [Fact]
    public void ExportsAndImportsRedefinedShortcuts()
    {
        var r = Sample();
        r.SetShortcut("query.run", KeyChord.Parse("Ctrl+Enter"));
        r.SetShortcut("tree.focus", null);

        var saved = r.ExportShortcutOverrides();
        Assert.Equal("Ctrl+Enter", saved["query.run"]);
        Assert.Equal(string.Empty, saved["tree.focus"]);

        var other = Sample();
        saved["does.not.exist"] = "F2";
        saved["tree.focusTableFilter"] = "Hyper+X";
        var ignored = other.ImportShortcutOverrides(saved);

        Assert.Equal(["does.not.exist", "tree.focusTableFilter"], ignored.Order());
        Assert.Equal(KeyChord.Parse("Ctrl+Enter"), other.ShortcutOf("query.run"));
        Assert.Null(other.ShortcutOf("tree.focus"));
        Assert.Equal(KeyChord.Parse("Ctrl+E"), other.ShortcutOf("tree.focusTableFilter"));
    }

    [Fact]
    public void ConflictsForANewShortcut()
    {
        var r = Sample();

        Assert.Equal(["tree.focusTableFilter"], r.ConflictsFor("tree.focus", KeyChord.Parse("Ctrl+E")).Select(c => c.Id));
        Assert.Empty(r.ConflictsFor("filter.apply", KeyChord.Parse("F9")));
    }
}
