using System.Globalization;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution.Editing;
using Eunomia.Engine.Results;
using Eunomia.UI.Controls.Grid;

namespace Eunomia.UI.Shell;

/// <summary>
/// Resultados editáveis nas query tabs (anexo D B18, <c>IsEditable</c>): quando todas as colunas vêm da mesma tabela
/// (origem dada pelo servidor) e a chave dela está no resultado, o grid grava como o Data tab (UPDATE/INSERT pela chave,
/// verificação das linhas afetadas e releitura). Senão, fica só de leitura e diz porquê.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Liga a edição de um result tab já completo, se o resultado for editável.</summary>
    private async Task EnableResultEditingAsync(ResultView entry, OpenSession session)
    {
        var grid = entry.Grid.Control;
        grid.EditRefused += (_, _) => ShowEditError(session.Definition.ReadOnly ? ReadOnlyMessage(session) : NoKeyMessage);
        if (session.Definition.ReadOnly)
        {
            return;
        }
        var schema = entry.Result.Source.Schema;
        var origins = schema.Columns.Select(c => (c.SourceDatabase, c.SourceTable, c.SourceColumn)).Distinct().ToList();
        if (schema.Count == 0 || schema.Columns.Any(c => c.SourceTable is null || c.SourceColumn is null || c.SourceDatabase is null)
            || schema.Columns.Select(c => (c.SourceDatabase, c.SourceTable)).Distinct().Count() != 1 || origins.Count != schema.Count)
        {
            return;
        }
        var origin = schema[0];
        TableDefinition table;
        try
        {
            table = await session.Metadata.GetTableAsync(new DbObjectRef(origin.SourceDatabase!, origin.SourceDatabase!, origin.SourceTable!, DbObjectKind.Table), CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbOperationException or Engine.Execution.SessionOperationException or InvalidOperationException)
        {
            return;
        }
        var columns = schema.Columns.Select(c => c.SourceColumn!).ToList();
        if (!RowKey.Of(table).Columns.All(k => columns.Contains(k.Name, StringComparer.OrdinalIgnoreCase)))
        {
            return;
        }
        entry.EditTable = table;
        grid.IsReadOnly = false;
        grid.CommitHandler = commit => CommitResultRowAsync(entry, session, table, columns, commit);
    }

    /// <summary>Valor atual de uma célula do resultado: o gravado por cima, senão o do source (texto bruto, binário completo).</summary>
    private static async Task<CellLiteral> ResultValueAsync(DataGridControl grid, long row, int column)
    {
        if (grid.Edits.TryGetCommitted(row, column, out var committed))
        {
            return new CellLiteral(committed.Text, committed.IsFunction);
        }
        if (grid.Source is not { } source || row >= source.RowCount.Count)
        {
            return CellLiteral.Null;
        }
        var block = await source.GetBlockAsync((int)(row / source.BlockSize));
        var r = (int)(row - block.FirstRow);
        if (block.IsNull(r, column))
        {
            return CellLiteral.Null;
        }
        return new CellLiteral(DisplayFormatter.FormatRaw(block, r, column));
    }

    private async Task<GridCommitResult> CommitResultRowAsync(ResultView entry, OpenSession session, TableDefinition table, IReadOnlyList<string> columns, GridRowCommit commit)
    {
        var grid = entry.Grid.Control;
        var changes = commit.Values.ToDictionary(v => columns[v.Key], v => new CellLiteral(v.Value.Text, v.Value.IsFunction), StringComparer.OrdinalIgnoreCase);
        _context.Status.Busy = "Saving row ...";
        try
        {
            var original = new Dictionary<string, CellLiteral>(StringComparer.OrdinalIgnoreCase);
            var expected = new Dictionary<string, CellLiteral>(StringComparer.OrdinalIgnoreCase);
            if (!commit.IsInsert)
            {
                foreach (var key in RowKey.Of(table).Columns)
                {
                    var index = columns.ToList().FindIndex(c => string.Equals(c, key.Name, StringComparison.OrdinalIgnoreCase));
                    original[key.Name] = await ResultValueAsync(grid, commit.Row, index);
                }
                foreach (var name in changes.Keys)
                {
                    expected[name] = await ResultValueAsync(grid, commit.Row, columns.ToList().FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)));
                }
            }
            var writer = new RowWriter(session.Host);
            var change = new RowChange(commit.IsInsert ? RowChangeKind.Insert : RowChangeKind.Update, original, changes) { Expected = expected };
            var written = await WriteWithConflictCheckAsync(writer, table, change);
            if (written is null)
            {
                return GridCommitResult.Failed;
            }
            var reread = await writer.ReadRowAsync(table, written.KeyAfter, CancellationToken.None);
            if (reread is null)
            {
                return new GridCommitResult(true);
            }
            var row = new Dictionary<int, CellValue>();
            for (var i = 0; i < columns.Count; i++)
            {
                if (reread.TryGetValue(columns[i], out var value))
                {
                    row[i] = value is null ? CellValue.Null : new CellValue(value);
                }
            }
            return new GridCommitResult(true, row);
        }
        catch (Exception ex) when (ex is RowWriteException or DbOperationException or Engine.Execution.SessionOperationException)
        {
            ShowEditError(ex.Message);
            return GridCommitResult.Failed;
        }
        finally
        {
            _context.Status.SetIdle();
        }
    }
}
