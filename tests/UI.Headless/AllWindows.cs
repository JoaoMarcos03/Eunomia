using Avalonia.Controls;
using Eunomia.App.Secrets;
using Eunomia.App.Sessions;
using Eunomia.App.Settings;
using Eunomia.App.Transfer;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;
using Eunomia.Engine.Transfer;
using Eunomia.Presentation.Sessions;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Features.Sessions;
using Eunomia.UI.Features.Transfer;
using Eunomia.UI.Shell;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Every top-level window of the application, created with minimal data (brand, accessibility).</summary>
internal static class AllWindows
{
    /// <summary>One factory per window; a new window without an entry here fails <c>BrandTests</c>.</summary>
    public static IReadOnlyDictionary<Type, Func<Window>> Factories { get; } = new Dictionary<Type, Func<Window>>
    {
        [typeof(MainWindow)] = () => new MainWindow(TestShell.CreateContext()),
        [typeof(AboutDialog)] = () => new AboutDialog("0.13.1"),
        [typeof(PreferencesWindow)] = () => new PreferencesWindow(TestShell.CreateContext()),
        [typeof(SearchReplaceDialog)] = () => new SearchReplaceDialog("x", replace: false, [new SearchTarget("Query #1", true, false)], 0, new SearchSettings()),
        [typeof(CreateDatabaseDialog)] = () => new CreateDatabaseDialog(["utf8mb4_general_ci"], null, null, (db, _) => db),
        [typeof(SqlFileRunWindow)] = () => new SqlFileRunWindow(1024),
        [typeof(LargeFilesDialog)] = () => new LargeFilesDialog([(@"C:\a.sql", 1L)]),
        [typeof(TextEditorWindow)] = () => new TextEditorWindow("Edit text", "x", null, binary: false),
        [typeof(ChoiceDialog)] = () => new ChoiceDialog("Choice", "Pick:", ["a", "b"], "a"),
        [typeof(SqlHelpWindow)] = () => new SqlHelpWindow((_, _) => Task.FromResult(new SqlHelp(null, null, null, [])), null),
        [typeof(FindTextDialog)] = () => new FindTextDialog(new FindTextServices(
            new MySqlDialect(MySqlConnectionFactory.ParseIdentity("10.4.32-MariaDB", null)), ["shop"], "shop", null,
            (_, _) => Task.FromResult<IReadOnlyList<(TableDefinition, long?)>>([]), (_, _) => Task.FromResult(0L), _ => { })),
        [typeof(MessageDialog)] = () => new MessageDialog("Confirm", "Sure?", MessageDialogButtons.OkCancel),
        [typeof(InputDialog)] = () => new InputDialog("Rename", "Name:", "x"),
        [typeof(LoginDialog)] = () => new LoginDialog("localhost", "root"),
        [typeof(SessionManagerWindow)] = () => new SessionManagerWindow(new SessionManagerServices
        {
            Model = new SessionManagerModel(new SessionCatalog(), new MemoryStore(), new MemorySecretStore()),
            Options = new SessionManagerOptions(),
            OpenAsync = (_, _, _) => Task.FromResult(false),
            Version = "0.13.1",
        }),
        [typeof(GridExportDialog)] = () => new GridExportDialog(new ExportOptions(), 1, 1, null, f => f),
        [typeof(SqlExportDialog)] = () => new SqlExportDialog(new SqlExportSettings(), ["shop"],
            (_, _) => Task.FromResult<IReadOnlyList<DbObjectInfo>>([]), (_, _, _) => throw new InvalidOperationException()),
        [typeof(CommandPaletteWindow)] = () => new CommandPaletteWindow(CommandPaletteTests.Sample),
        [typeof(CopyTableDialog)] = () => new CopyTableDialog(CopyTableDialogTests.Fake()),
        [typeof(MaintenanceDialog)] = () => new MaintenanceDialog(MaintenanceDialogTests.Fake()),
        [typeof(CsvImportDialog)] = () => new CsvImportDialog(new CsvImportSettings(), ["shop"],
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]), (_, _, _) => Task.FromResult<IReadOnlyList<CsvTargetColumn>>([]),
            (_, _, _) => throw new InvalidOperationException()),
    };

    private sealed class MemoryStore : ISessionStore
    {
        public SessionCatalog Load() => new();

        public void Save(SessionCatalog catalog)
        {
        }
    }
}
