using System.Globalization;
using Eunomia.App.Sessions;
using Eunomia.App.Transfer;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Transfer;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Features.Transfer;

namespace Eunomia.UI.Shell;

/// <summary>
/// Tools › Import CSV file (Fase 8; FAM-E07): o diálogo com a base e a tabela focadas na árvore já escolhidas, e o
/// <see cref="CsvImportJob"/> na sessão ativa. O SQL log regista o resultado.
/// </summary>
public sealed partial class MainWindow
{
    private void InitCsvImport() =>
        _context.Commands.SetHandler("import.csv", _ => new ValueTask(ImportCsvAsync()), () => ActiveSession is { Host.State: SessionState.Ready });

    private async Task ImportCsvAsync()
    {
        if (ActiveSession is not { Host.State: SessionState.Ready } open)
        {
            return;
        }
        IReadOnlyList<string> databases;
        try
        {
            databases = await open.Metadata.GetDatabasesAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, e.Message);
            return;
        }
        var focused = _tree.Tree.FocusedNode;
        var database = focused?.Database?.Name ?? open.Host.CurrentDatabase;
        var table = focused is { Kind: UI.Controls.Tree.TreeNodeKind.Table } ? focused.Name : null;
        await CsvImportDialog.ShowAsync(
            this,
            _context.Settings.CsvImport,
            databases,
            async (db, ct) => [.. (await open.Metadata.GetObjectsAsync(db, ct)).Objects.Where(o => o.Ref.Kind == DbObjectKind.Table).Select(o => o.Ref.Name)],
            async (db, name, ct) =>
            {
                var definition = await open.Metadata.GetTableAsync(new DbObjectRef(db, db, name, DbObjectKind.Table), ct);
                return [.. definition.Columns.Where(c => !c.IsGenerated).Select(c => new CsvTargetColumn(c.Name, c.Type.Kind is LogicalTypeKind.Integer or LogicalTypeKind.Decimal or LogicalTypeKind.Float))];
            },
            (request, progress, ct) => RunCsvImportAsync(open, request, progress, ct),
            database,
            table);
        _context.SaveSettings();
    }

    private async Task<CsvImportResult> RunCsvImportAsync(OpenSession open, CsvImportRequest request, IProgress<CsvImportProgress> progress, CancellationToken cancellationToken)
    {
        if (!await ConfirmWritesAsync(open, $"Import \"{Path.GetFileName(request.Path)}\" into {request.Database}.{request.Table}?"))
        {
            return new CsvImportResult(Cancelled: true, 0, 0, 0, 0, TimeSpan.Zero);
        }
        try
        {
            var result = await Task.Run(() => new CsvImportJob(open.Host, request).RunAsync(progress, cancellationToken), CancellationToken.None);
            _context.Log.Append(result.Cancelled ? LogCategory.Error : LogCategory.Info, string.Create(CultureInfo.InvariantCulture,
                $"CSV import of {request.Path} into {request.Database}.{request.Table} {(result.Cancelled ? "cancelled" : "finished")}: {result.Rows} rows, {result.Warnings} warnings, {result.Elapsed.TotalSeconds:0.0} s."));
            _context.Diagnostics.Write(Eunomia.App.Diagnostics.DiagnosticLevel.Info, "import",
                $"CSV import ({request.Method}) of {request.Path}: {result.Rows} rows, {result.BytesRead} bytes, cancelled: {result.Cancelled}");
            if (result.Rows > 0)
            {
                open.Metadata.InvalidateTable(new DbObjectRef(request.Database, request.Database, request.Table, DbObjectKind.Table));
            }
            return result;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, $"CSV import of {request.Path}: {e.Message}");
            throw;
        }
    }
}
