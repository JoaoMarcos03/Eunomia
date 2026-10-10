using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Eunomia.App.Settings;
using Eunomia.App.Transfer;
using Eunomia.Engine.Transfer;
using Eunomia.UI.Features.Transfer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Import CSV file dialog (phase 8; FAM-E07; appendix B §3.1).</summary>
public sealed class CsvImportDialogTests
{
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task DefaultsDoHeidiSqlMapeamentoPosicionalEImport()
    {
        var file = Path.Combine(Path.GetTempPath(), "eunomia-csv-" + Guid.NewGuid().ToString("N")[..8] + ".csv");
        await File.WriteAllTextAsync(file, "a;b\r\n1;2\r\n");
        try
        {
            var settings = new CsvImportSettings { FileName = file };
            CsvImportRequest? requested = null;
            var dialog = new CsvImportDialog(
                settings,
                ["loja", "outra"],
                (db, _) => Task.FromResult<IReadOnlyList<string>>(db == "loja" ? ["clientes", "produtos"] : ["t"]),
                (_, table, _) => Task.FromResult<IReadOnlyList<CsvTargetColumn>>(table == "produtos"
                    ? [new("id", true), new("nome", false), new("preco", true)]
                    : [new("id", true)]),
                (request, progress, _) =>
                {
                    requested = request;
                    progress.Report(new CsvImportProgress(5, 10, 1, TimeSpan.FromSeconds(1)));
                    return Task.FromResult(new CsvImportResult(false, 1, 0, 10, 10, TimeSpan.FromSeconds(1)));
                },
                initialDatabase: "loja",
                initialTable: "produtos");
            dialog.Show();
            await dialog.Loading;
            await RealServer.WaitAsync(() => dialog.ColumnItems.Count == 3 ? "ok" : null);

            // Defaults: ";", optional '"', '"', \r\n, REPLACE, server.
            Assert.Equal("produtos", dialog.TableCombo.SelectedItem);
            Assert.All(dialog.ColumnItems, i => Assert.True(i.Check.IsChecked));
            Assert.True(dialog.OptionallyBox.IsEnabled);
            dialog.ClientRadio.IsChecked = true;
            Assert.False(dialog.OptionallyBox.IsEnabled);

            // Field k goes to the k-th checked column: "preco" moves above "nome" and "id" is left out.
            dialog.MoveColumn(2, -1);
            dialog.ColumnItems[0].Check.IsChecked = false;
            dialog.ImportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await RealServer.WaitAsync(() => requested);

            Assert.Equal(file, requested!.Path);
            Assert.Equal(["preco", "nome"], requested.Columns.Select(c => c.Name));
            Assert.True(requested.Columns[0].IsNumeric);
            Assert.Equal(";", requested.Csv.FieldTerminator);
            Assert.Equal("\"", requested.Csv.Encloser);
            Assert.Equal("\r\n", requested.Csv.LineTerminator);
            Assert.Equal(1, requested.Csv.IgnoreLines);
            Assert.Equal(CsvDuplicates.Replace, requested.Duplicates);
            Assert.Equal(CsvImportMethod.Client, requested.Method);
            Assert.True(requested.LowPriority);
            Assert.False(requested.Truncate);
            // Without "Keep dialog open", it closes after an import with rows; the mode is saved.
            await RealServer.WaitAsync(() => dialog.IsVisible ? null : "closed");
            Assert.Equal(1, settings.Method);
            Assert.Equal(@"\r\n", settings.LineTerminator);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
