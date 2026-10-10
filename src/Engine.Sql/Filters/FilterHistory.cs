namespace Eunomia.Engine.Sql.Filters;

/// <summary>
/// History of a tree filter box (section 4.3): up to 20 entries, the most recent first,
/// global (not per session). The UI saves when the box loses focus; "Clear" clears the box and
/// "Empty recent filters" calls <see cref="Clear"/>.
/// </summary>
public sealed class FilterHistory
{
    public const int Capacity = 20;

    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Adds at the top. Empty text is ignored; a repeated text moves to the top.</summary>
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
