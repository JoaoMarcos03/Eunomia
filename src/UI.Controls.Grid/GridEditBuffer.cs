namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Valor de célula editado: <see cref="Text"/> <c>null</c> = NULL; com <see cref="IsFunction"/>, uma expressão SQL que vai
/// tal como está (Insert value ▸ SQL function, Ctrl+F2).
/// </summary>
public readonly record struct CellValue(string? Text, bool IsFunction = false)
{
    public static CellValue Null => new(null);

    public bool IsNull => Text is null && !IsFunction;
}

/// <summary>Linha a gravar: índice no grid, se é nova e os valores alterados por coluna do grid.</summary>
public sealed record GridRowCommit(long Row, bool IsInsert, IReadOnlyDictionary<int, CellValue> Values);

/// <summary>Pedido do editor em janela para uma célula: valor atual e se é binário (hex).</summary>
public sealed record GridPopupEditEventArgs(long Row, int Column, string? Value, bool IsBinary);

/// <summary>Resultado de uma gravação: se gravou e os valores relidos do servidor (ficam por cima do source).</summary>
public sealed record GridCommitResult(bool Ok, IReadOnlyDictionary<int, CellValue>? Row = null)
{
    public static GridCommitResult Failed { get; } = new(false);
}

/// <summary>
/// Edit buffer separado dos blocos (secções 11.5 e 15.1, EditModel): as alterações acumulam-se numa linha
/// e são gravadas ao mudar de linha ou com Ctrl+Enter; mudar de coluna na mesma linha não grava (FAM-G08).
/// Linhas novas (↓ na última linha) são acrescentadas no fim. No spike, "gravar" passa os valores para um
/// overlay de valores gravados (não há servidor) e dispara <see cref="RowCommitted"/>.
/// </summary>
public sealed class GridEditBuffer
{
    private readonly Dictionary<int, CellValue> _pending = [];
    private readonly Dictionary<long, Dictionary<int, CellValue>> _committed = [];

    /// <summary>Linha com alterações por gravar, ou -1.</summary>
    public long PendingRow { get; private set; } = -1;

    /// <summary>Linhas novas acrescentadas depois das linhas do source.</summary>
    public long InsertedRows { get; private set; }

    /// <summary>A linha pendente é uma linha nova ainda não gravada.</summary>
    public bool PendingIsInsert { get; private set; }

    public bool HasPending => PendingRow >= 0;

    public event EventHandler<long>? RowCommitted;

    /// <summary>Valor editado (pendente ou gravado) da célula, se existir.</summary>
    public bool TryGetValue(long row, int column, out CellValue value)
    {
        if (row == PendingRow && _pending.TryGetValue(column, out value))
        {
            return true;
        }
        if (_committed.TryGetValue(row, out var cells) && cells.TryGetValue(column, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Valores por gravar da linha pendente, por coluna.</summary>
    public IReadOnlyDictionary<int, CellValue> PendingValues => _pending;

    /// <summary>Valor já gravado (não o pendente) da célula, se existir.</summary>
    public bool TryGetCommitted(long row, int column, out CellValue value)
    {
        if (_committed.TryGetValue(row, out var cells) && cells.TryGetValue(column, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Valor gravado no servidor e relido, por cima do source (até o grid mudar de dados).</summary>
    public void SetCommitted(long row, int column, CellValue value)
    {
        if (!_committed.TryGetValue(row, out var cells))
        {
            cells = [];
            _committed[row] = cells;
        }
        cells[column] = value;
    }

    /// <summary>Célula com alteração por gravar (triângulo vermelho).</summary>
    public bool IsModified(long row, int column) => row == PendingRow && _pending.ContainsKey(column);

    public void SetValue(long row, int column, CellValue value)
    {
        if (PendingRow >= 0 && PendingRow != row)
        {
            throw new InvalidOperationException("Há alterações por gravar noutra linha; chamar Commit() primeiro.");
        }
        PendingRow = row;
        _pending[column] = value;
    }

    /// <summary>Acrescenta uma linha nova no índice <paramref name="sourceRows"/> + linhas já inseridas.</summary>
    public long InsertRow(long sourceRows)
    {
        if (HasPending)
        {
            throw new InvalidOperationException("Gravar a linha pendente antes de inserir.");
        }
        var row = sourceRows + InsertedRows;
        InsertedRows++;
        PendingRow = row;
        PendingIsInsert = true;
        return row;
    }

    /// <summary>Grava a linha pendente. Devolve <c>false</c> se não havia nada para gravar.</summary>
    public bool Commit()
    {
        if (!HasPending)
        {
            return false;
        }
        var row = PendingRow;
        if (!_committed.TryGetValue(row, out var cells))
        {
            cells = [];
            _committed[row] = cells;
        }
        foreach (var (column, value) in _pending)
        {
            cells[column] = value;
        }
        ClearPending();
        RowCommitted?.Invoke(this, row);
        return true;
    }

    /// <summary>Esquece tudo (pendente, gravado em memória e linhas novas): o grid mudou de dados.</summary>
    public void Reset()
    {
        ClearPending();
        _committed.Clear();
        InsertedRows = 0;
    }

    /// <summary>Esc fora de edição: descarta as alterações da linha (e a linha, se for nova).</summary>
    public void Discard()
    {
        if (PendingIsInsert)
        {
            InsertedRows--;
        }
        ClearPending();
    }

    private void ClearPending()
    {
        _pending.Clear();
        PendingRow = -1;
        PendingIsInsert = false;
    }
}
