namespace Eunomia.App.Query;

/// <summary>State of a query tab saved for restoring (appendix C §4.3; FAM-Q07; D9).</summary>
public sealed record TabState
{
    public Guid Id { get; init; }

    /// <summary>Position among the query tabs.</summary>
    public int Order { get; init; }

    public string Caption { get; init; } = string.Empty;

    /// <summary>File loaded or saved in the tab, if any.</summary>
    public string? FilePath { get; init; }

    public string? FileEncoding { get; init; }

    /// <summary>Date (UTC ticks) and size of the file at the last Load or Save: if it changed since, asks "Reload".</summary>
    public long? FileWriteTicks { get; init; }

    public long? FileLength { get; init; }

    /// <summary>Changed since the last Save (the caption's <c>*</c>).</summary>
    public bool Modified { get; init; }

    /// <summary>"Pin tab to session" (D9).</summary>
    public Guid? PinnedSession { get; init; }

    public string? PinnedName { get; init; }

    public int CaretOffset { get; init; }

    /// <summary>It was the active tab.</summary>
    public bool Focused { get; init; }
}

/// <summary>A change to the text, in the order it happened (positions in the text as it was before it).</summary>
public readonly record struct TextEdit(int Offset, int RemovedLength, string Inserted);

/// <summary>A recovered tab: the state and the text.</summary>
public sealed record RestoredTab(TabState State, string Text);

/// <summary>
/// Query tab journal (section 21.4; TR-31; PR-21): the state of each tab and the changes to its text, written at every
/// <see cref="Flush"/> (every 2 s) with fsync, to recover after a crash or on reopening.
/// </summary>
public interface ITabJournal
{
    /// <summary>
    /// Saved tabs that do not belong to another running instance, in order; they become this instance's.
    /// </summary>
    IReadOnlyList<RestoredTab> Restore();

    /// <summary>Saves the state of a tab (atomic write).</summary>
    void SaveState(TabState state);

    /// <summary>Adds a change to the text (kept in memory until the next <see cref="Flush"/>).</summary>
    void Append(Guid tab, TextEdit edit);

    /// <summary>The tab's text becomes exactly <paramref name="text"/> (for example, when restoring or loading).</summary>
    void Reset(Guid tab, string text);

    /// <summary>Writes what is pending and forces it to disk. Can run off the UI thread.</summary>
    void Flush();

    /// <summary>The tab closed: deletes its state and text.</summary>
    void Remove(Guid tab);
}
