using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Headless.Tests;

/// <summary>The target dialog of Run SQL file and its searchable database select.</summary>
public sealed class RunSqlFileDialogTests
{
    private static readonly string[] Databases = ["crm", "shop", "shop_archive", "Stats"];

    private static (Window Window, SearchableSelect Select, Button Other) ShowSelect(string? selected = "crm")
    {
        var select = new SearchableSelect { Items = Databases, SelectedItem = selected };
        var other = new Button { Content = "Other" };
        var panel = new StackPanel();
        panel.Children.Add(select);
        panel.Children.Add(other);
        var window = new Window { Width = 400, Height = 300, Content = panel };
        window.Show();
        return (window, select, other);
    }

    private static void Type(Window window, SearchableSelect select, string text)
    {
        select.Box.Focus();
        select.Box.Text = string.Empty;
        window.KeyTextInput(text);
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public void TheFilterKeepsWhatContainsTheTextIgnoringCase()
    {
        var (window, select, _) = ShowSelect();
        var filter = select.Box.ItemFilter!;

        Assert.Equal(["shop", "shop_archive"], Databases.Where(d => filter("SHO", d)));
        Assert.Equal(["shop_archive"], Databases.Where(d => filter("arch", d)));
        // Nothing typed, or the text is still the chosen item: the whole list (the arrow shows it all).
        Assert.Equal(Databases, Databases.Where(d => filter(string.Empty, d)));
        Assert.Equal(Databases, Databases.Where(d => filter("crm", d)));
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public void TypedTextChoosesTheItemItNamesOrTheOnlyOneThatContainsIt()
    {
        var (window, select, other) = ShowSelect();
        var changes = 0;
        select.SelectionChanged += (_, _) => changes++;

        // The only item that contains the text, when the box loses the focus.
        Type(window, select, "arch");
        other.Focus();
        Assert.Equal(("shop_archive", "shop_archive", 1), (select.SelectedItem, select.Box.Text, changes));

        // The exact name wins over the items that contain it, ignoring case.
        Type(window, select, "SHOP");
        other.Focus();
        Assert.Equal(("shop", "shop", 2), (select.SelectedItem, select.Box.Text, changes));

        // Enter, with the list closed.
        Type(window, select, "stats");
        select.Box.IsDropDownOpen = false;
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal("Stats", select.SelectedItem);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public void TextThatNamesNoItemGoesBackAndEmptyTextIsNone()
    {
        var (window, select, other) = ShowSelect();

        // "shop" is in two items: not chosen; "zzz" in none.
        foreach (var text in new[] { "zzz", "sh" })
        {
            Type(window, select, text);
            other.Focus();
            Assert.Equal(("crm", "crm"), (select.SelectedItem, select.Box.Text));
        }

        Type(window, select, "   ");
        other.Focus();
        Assert.Null(select.SelectedItem);

        // An item picked in the list; one that is not in the list cannot be set.
        select.Box.SelectedItem = "shop";
        Assert.Equal("shop", select.SelectedItem);
        select.SelectedItem = "missing";
        Assert.Equal((null, string.Empty), (select.SelectedItem, select.Box.Text));
        // New items keep the chosen one only if it is still there.
        select.SelectedItem = "crm";
        select.Items = ["crm", "other"];
        Assert.Equal("crm", select.SelectedItem);
        select.Items = ["other"];
        Assert.Null(select.SelectedItem);
        window.Close();
    }

    /// <summary>
    /// A click in the box selects the chosen item's text, so typing searches instead of adding to it (the click puts the
    /// caret after the box's own select-all on focus).
    /// </summary>
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public void TypingAfterAClickReplacesTheChosenItem()
    {
        var (window, select, other) = ShowSelect("shop");
        Dispatcher.UIThread.RunJobs();
        var point = select.Box.TranslatePoint(new Point(12, select.Box.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(select.Box.IsKeyboardFocusWithin);
        window.KeyTextInput("arch");
        Assert.Equal("arch", select.Box.Text);
        Assert.Equal(["shop_archive"], Databases.Where(d => select.Box.ItemFilter!(select.Box.Text, d)));
        other.Focus();
        Assert.Equal("shop_archive", select.SelectedItem);
        window.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public void AFileWithoutUseOnlyAsksForTheDatabase()
    {
        var dialog = new RunSqlFileDialog("it (127.0.0.1:3306)", [@"C:\a.sql", @"C:\b.sql"], Databases, "shop", []);
        dialog.Show();

        Assert.Equal("a.sql\nb.sql", dialog.Files);
        Assert.Equal("shop", dialog.DatabaseSelect.SelectedItem);
        // No USE in the file: no choice to make about it.
        Assert.Null(dialog.AsFileOption.Parent);
        Assert.Null(dialog.StayOption.Parent);
        Assert.Equal(new RunSqlFileTarget("shop", false), dialog.Result);

        dialog.DatabaseSelect.SelectedItem = null;
        Assert.StartsWith("No database:", dialog.Hint, StringComparison.Ordinal);
        Assert.Equal(new RunSqlFileTarget(null, false), dialog.Result);
        dialog.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public void AFileWithUseCanRunAsInTheFileOrInTheChosenDatabase()
    {
        var files = Enumerable.Range(1, 7).Select(i => $@"C:\dumps\part{i}.sql").ToList();
        var dialog = new RunSqlFileDialog("it (127.0.0.1:3306)", files, Databases, null, ["shop"]);
        dialog.Show();

        Assert.EndsWith("part5.sql\n... and 2 more", dialog.Files, StringComparison.Ordinal);
        Assert.NotNull(dialog.StayOption.Parent);
        Assert.True(dialog.AsFileOption.IsChecked);
        // Without a database there is nowhere to run everything.
        Assert.False(dialog.StayOption.IsEnabled);
        Assert.Equal(new RunSqlFileTarget(null, false), dialog.Result);

        dialog.DatabaseSelect.SelectedItem = "crm";
        Assert.True(dialog.StayOption.IsEnabled);
        Assert.Equal(new RunSqlFileTarget("crm", false), dialog.Result);
        dialog.StayOption.IsChecked = true;
        Assert.Equal(new RunSqlFileTarget("crm", true), dialog.Result);

        // Clearing the database goes back to "as in the file".
        dialog.DatabaseSelect.SelectedItem = null;
        Assert.False(dialog.StayOption.IsEnabled);
        Assert.True(dialog.AsFileOption.IsChecked);
        Assert.Equal(new RunSqlFileTarget(null, false), dialog.Result);
        dialog.Close();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task RunReturnsTheChoiceAndEscapeCancels()
    {
        UnexpectedDialogGuardAttribute.Allow(typeof(RunSqlFileDialog));
        var owner = new Window();
        owner.Show();
        var dialog = new RunSqlFileDialog("it (127.0.0.1:3306)", [@"C:\a.sql"], Databases, "crm", ["shop"]);
        var shown = RunSqlFileDialog.ShowAsync(owner, dialog);
        dialog.StayOption.IsChecked = true;
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(new RunSqlFileTarget("crm", true), await shown);

        dialog = new RunSqlFileDialog("it (127.0.0.1:3306)", [@"C:\a.sql"], Databases, "crm", []);
        shown = RunSqlFileDialog.ShowAsync(owner, dialog);
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Null(await shown);
        owner.Close();
    }
}
