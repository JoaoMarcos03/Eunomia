using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Eunomia.App.Settings;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Features.Sessions;
using Eunomia.UI.Features.Transfer;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Phase 13: dialog snapshots (light and dark), to review the look of every screen.</summary>
public sealed class DialogSnapshotTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var name in new[] { "preferences", "preferences-grid", "search-replace", "create-database", "run-sql-file", "grid-export", "login", "text-editor", "message", "input", "large-files", "about" })
        {
            data.Add(name, "light");
            data.Add(name, "dark");
        }
        return data;
    }

    private static Window Create(string name) => name switch
    {
        "preferences" => new PreferencesWindow(TestShell.CreateContext()),
        "preferences-grid" => new PreferencesWindow(TestShell.CreateContext(), PreferencesWindow.DataPage),
        "search-replace" => new SearchReplaceDialog("customer", replace: true, [new SearchTarget("Query #1", true, false), new SearchTarget("Data grid", false, false)], 0, new SearchSettings()),
        "create-database" => new CreateDatabaseDialog(["utf8mb4_general_ci", "utf8mb4_unicode_ci", "latin1_swedish_ci"], "utf8mb4_unicode_ci", "utf8mb4_general_ci",
            (db, collation) => $"CREATE DATABASE `{db}`" + (collation is null ? string.Empty : $" /*!40100 COLLATE '{collation}' */")),
        "run-sql-file" => new SqlFileRunWindow(10L * 1024 * 1024),
        "grid-export" => new GridExportDialog(new ExportOptions(), 12, 12345, "name", f => f),
        "login" => new LoginDialog("localhost", "root"),
        "text-editor" => new TextEditorWindow("Edit text: notes", "Line one\nLine two with a longer text value", 65535, binary: false),
        "message" => new MessageDialog("Confirm", "Drop 2 object(s) in database \"shop\"?\n\ncustomers, orders", MessageDialogButtons.OkCancel),
        "input" => new InputDialog("Rename query tab", "New name:", "Query #2"),
        "large-files" => new LargeFilesDialog([(@"C:\dumps\shop-full.sql", 734_003_200L)]),
        "about" => new AboutDialog("0.13.1", ".NET 10.0.0, Avalonia 12"),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void Dialogo(string name, string theme)
    {
        var window = Create(name);
        window.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        window.Show();
        window.CaptureRenderedFrame();

        Snapshot.Match(window.CaptureRenderedFrame(), $"dialog-{name}-{theme}");
        window.Close();
    }
}
