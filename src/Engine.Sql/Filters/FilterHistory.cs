namespace Eunomia.Engine.Sql.Filters;

/// <summary>
/// Histórico de uma caixa de filtro da árvore (secção 4.3): até 20 entradas, a mais recente primeiro,
/// globais (não por sessão). A UI grava quando a caixa perde o foco; "Clear" limpa a caixa e
/// "Empty recent filters" chama <see cref="Clear"/>.
/// </summary>
public sealed class FilterHistory
{
    public const int Capacity = 20;

    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Acrescenta no topo. Texto vazio é ignorado; um texto repetido sobe para o topo.</summary>
    public void Add(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }
        _entries.Remove(text);
        _entries.Insert(0, text);
        if (_entries.Count > Capacity)
        {
            _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        }
    }

    public void Clear() => _entries.Clear();
}
