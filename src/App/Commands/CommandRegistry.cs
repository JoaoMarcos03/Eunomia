namespace Eunomia.App.Commands;

/// <summary>
/// Focus contexts in which a command is active (section 21.2). <see cref="Global"/> applies everywhere. The UI tags
/// controls with their context; the current focus gives a list from the innermost context to the outermost.
/// </summary>
public static class CommandContexts
{
    public const string Global = "global";
    public const string Tree = "tree";
    public const string Grid = "grid";
    public const string QueryEditor = "editor";

    /// <summary>Data tab WHERE editor (Apply filter with F9, Clear with Ctrl+W).</summary>
    public const string FilterPanel = "filterPanel";

    public const string TreeFilter = "treeFilter";
    public const string Log = "log";

    /// <summary>Host tab lists (Del = Kill process).</summary>
    public const string HostList = "hostList";

    /// <summary>Database tab object list (shares <c>popupDB</c> with the tree).</summary>
    public const string ObjectList = "objectList";

    /// <summary>Query tab helpers panel.</summary>
    public const string Helpers = "helpers";
}

/// <summary>Item kind: plain action, toggle (check) or option of a radio group.</summary>
public enum CommandKind
{
    Action,
    Toggle,
    Radio,
}

/// <summary>
/// A user action (section 21.2): stable id, caption, category, icon, default shortcut, contexts and predicate.
/// <see cref="HeidiSqlAction"/> keeps the name of the HeidiSQL action when known (to import shortcuts and to test
/// that the inventory is complete). Without a handler, the command shows disabled, with
/// <see cref="AvailableIn"/> in the tooltip, to keep the menu geography.
/// </summary>
public sealed record CommandDescriptor(
    string Id,
    string Caption,
    string Category,
    KeyChord? DefaultShortcut = null,
    IReadOnlyList<string>? Contexts = null,
    string? Icon = null,
    string? HeidiSqlAction = null,
    string? AvailableIn = null)
{
    /// <summary>Help text (HeidiSQL hint), shown in tooltips and in Preferences › Shortcuts.</summary>
    public string? Hint { get; init; }

    public CommandKind Kind { get; init; }

    /// <summary>Radio group (<see cref="CommandKind.Radio"/>).</summary>
    public string? RadioGroup { get; init; }

    /// <summary>Hidden in the menus (actions with <c>Visible=False</c> in HeidiSQL), but present in the inventory.</summary>
    public bool Hidden { get; init; }

    public IReadOnlyList<string> ActiveIn => Contexts is { Count: > 0 } ? Contexts : [CommandContexts.Global];

    public bool IsGlobal => ActiveIn.Contains(CommandContexts.Global);
}

/// <summary>Two commands with the same shortcut that can be active at the same time.</summary>
public sealed record ShortcutConflict(KeyChord Shortcut, string FirstId, string SecondId, string Context);

/// <summary>State of a command in the current context, for menus, toolbar and tooltips.</summary>
public sealed record CommandState(CommandDescriptor Command, bool IsImplemented, bool CanExecute, bool IsChecked, string? Tooltip, string Caption);

/// <summary>
/// Command registry (section 21.2): menus, toolbar, context menus and the keymap are generated from it. Shortcuts
/// resolve by focus context, as in HeidiSQL: a command of the focused context wins over a global one
/// (F9 = Run in the editor, Apply filter in the filter panel), and a command that cannot run does not consume the shortcut
/// (Esc = Cancel editing only with pending changes, otherwise Cancel running operation). Shortcuts redefined by the
/// user (Preferences › Shortcuts, FAM-K01) replace the default shortcut. Not thread-safe: use it on the UI thread.
/// </summary>
public sealed class CommandRegistry : ICommandBus
{
    private readonly Dictionary<string, CommandDescriptor> _commands = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<string, KeyChord?> _overrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<CancellationToken, ValueTask>> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<bool>> _canExecute = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<bool>> _checked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<string?>> _captions = new(StringComparer.Ordinal);

    public IEnumerable<CommandDescriptor> All => _order.Select(id => _commands[id]);

    /// <summary>Something changed (handler, shortcut, state): menus and toolbar must read <see cref="StateOf"/> again.</summary>
    public event EventHandler? StatesInvalidated;

    public void Register(CommandDescriptor command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_commands.TryAdd(command.Id, command))
        {
            throw new InvalidOperationException($"Comando duplicado: {command.Id}.");
        }
        _order.Add(command.Id);
    }

    public CommandDescriptor Get(string id) =>
        _commands.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException($"Comando desconhecido: {id}.");

    public bool TryGet(string id, out CommandDescriptor? command) => _commands.TryGetValue(id, out command);

    /// <summary>Command by the name of the HeidiSQL action (<c>actExecuteQuery</c>).</summary>
    public CommandDescriptor? FindByHeidiSqlAction(string action) =>
        All.FirstOrDefault(c => string.Equals(c.HeidiSqlAction, action, StringComparison.OrdinalIgnoreCase));

    /// <summary>Binds the command to a handler; without a handler it stays disabled ("available in version X").</summary>
    public void SetHandler(string id, Func<CancellationToken, ValueTask> handler, Func<bool>? canExecute = null)
    {
        Get(id);
        _handlers[id] = handler;
        if (canExecute is null)
        {
            _canExecute.Remove(id);
        }
        else
        {
            _canExecute[id] = canExecute;
        }
        InvalidateStates();
    }

    /// <summary>Synchronous handler (shorthand for most UI commands).</summary>
    public void SetHandler(string id, Action handler, Func<bool>? canExecute = null) =>
        SetHandler(id, _ => { handler(); return ValueTask.CompletedTask; }, canExecute);

    /// <summary>Check state of a toggle or radio item.</summary>
    public void SetChecked(string id, Func<bool> isChecked)
    {
        Get(id);
        _checked[id] = isChecked;
    }

    /// <summary>
    /// Caption computed when the menu opens (Quick Filter with the real SQL, "Select top N rows"); <c>null</c> uses the
    /// catalog's.
    /// </summary>
    public void SetCaption(string id, Func<string?> caption)
    {
        Get(id);
        _captions[id] = caption;
    }

    /// <summary>Effective shortcut: the one redefined by the user, if any, otherwise the default.</summary>
    public KeyChord? ShortcutOf(string id) =>
        _overrides.TryGetValue(id, out var custom) ? custom : Get(id).DefaultShortcut;

    /// <summary>Redefines the shortcut (<c>null</c> removes it). To go back to the default: <see cref="ResetShortcut"/>.</summary>
    public void SetShortcut(string id, KeyChord? shortcut)
    {
        Get(id);
        _overrides[id] = shortcut;
        InvalidateStates();
    }

    public void ResetShortcut(string id)
    {
        _overrides.Remove(id);
        InvalidateStates();
    }

    /// <summary>Redefined shortcuts, in the settings format (id → "Ctrl+X", or empty when removed).</summary>
    public Dictionary<string, string> ExportShortcutOverrides() =>
        _overrides.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty, StringComparer.Ordinal);

    /// <summary>
    /// Applies shortcuts from the settings. Unknown ids (a command removed in a newer version) and invalid texts are
    /// ignored and returned, for the log.
    /// </summary>
    public IReadOnlyList<string> ImportShortcutOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        var ignored = new List<string>();
        _overrides.Clear();
        foreach (var (id, text) in overrides)
        {
            if (!_commands.ContainsKey(id))
            {
                ignored.Add(id);
                continue;
            }
            if (text.Length == 0)
            {
                _overrides[id] = null;
            }
            else if (KeyChord.TryParse(text, out var chord))
            {
                _overrides[id] = chord;
            }
            else
            {
                ignored.Add(id);
            }
        }
        InvalidateStates();
        return ignored;
    }

    public CommandState StateOf(string id)
    {
        var command = Get(id);
        var implemented = _handlers.ContainsKey(id);
        var can = implemented && (!_canExecute.TryGetValue(id, out var predicate) || predicate());
        var isChecked = _checked.TryGetValue(id, out var check) && check();
        var tooltip = implemented ? null : command.AvailableIn is { } version ? $"Available in version {version}" : "Not available yet";
        var caption = _captions.TryGetValue(id, out var dynamic) ? dynamic() : null;
        return new CommandState(command, implemented, can, isChecked, tooltip, caption ?? command.Caption);
    }

    /// <summary>
    /// Command for a shortcut in the focus context: first the ones active in that context, then the global ones.
    /// Returns <c>null</c> if none matches. Does not look at <c>CanExecute</c>: see <see cref="ResolveExecutable"/>.
    /// </summary>
    public CommandDescriptor? Resolve(KeyChord shortcut, string focusContext) =>
        Candidates(shortcut, [focusContext]).FirstOrDefault();

    /// <summary>
    /// Command the shortcut runs now: the first one that can run, searching the focus contexts (from the innermost
    /// to the outermost) and then the global ones. Disabled commands do not consume the shortcut.
    /// </summary>
    public CommandDescriptor? ResolveExecutable(KeyChord shortcut, IReadOnlyList<string> focusContexts) =>
        Candidates(shortcut, focusContexts).FirstOrDefault(c => StateOf(c.Id).CanExecute);

    private IEnumerable<CommandDescriptor> Candidates(KeyChord shortcut, IReadOnlyList<string> focusContexts)
    {
        var matching = _order.Where(id => ShortcutOf(id) == shortcut).Select(id => _commands[id]).ToList();
        foreach (var context in focusContexts)
        {
            foreach (var command in matching)
            {
                if (!command.IsGlobal && command.ActiveIn.Contains(context))
                {
                    yield return command;
                }
            }
        }
        foreach (var command in matching)
        {
            if (command.IsGlobal)
            {
                yield return command;
            }
        }
    }

    /// <summary>
    /// Conflicts that Preferences has to show (FAM-K01): the same shortcut on two commands active in the same
    /// context, or both global. A context command with the same shortcut as a global one is not a conflict:
    /// the context one wins.
    /// </summary>
    public IReadOnlyList<ShortcutConflict> FindConflicts()
    {
        var result = new List<ShortcutConflict>();
        var withShortcut = _order.Select(id => (Id: id, Shortcut: ShortcutOf(id))).Where(x => x.Shortcut is not null).ToList();
        for (var i = 0; i < withShortcut.Count; i++)
        {
            for (var j = i + 1; j < withShortcut.Count; j++)
            {
                if (withShortcut[i].Shortcut != withShortcut[j].Shortcut)
                {
                    continue;
                }
                var a = _commands[withShortcut[i].Id];
                var b = _commands[withShortcut[j].Id];
                var shared = a.ActiveIn.Intersect(b.ActiveIn, StringComparer.Ordinal).FirstOrDefault();
                if (shared is not null)
                {
                    result.Add(new ShortcutConflict(withShortcut[i].Shortcut!.Value, a.Id, b.Id, shared));
                }
            }
        }
        return result;
    }

    /// <summary>Commands that already use <paramref name="shortcut"/> in a context shared with <paramref name="id"/>.</summary>
    public IReadOnlyList<CommandDescriptor> ConflictsFor(string id, KeyChord shortcut)
    {
        var command = Get(id);
        return All.Where(c => c.Id != id && ShortcutOf(c.Id) == shortcut
                              && c.ActiveIn.Intersect(command.ActiveIn, StringComparer.Ordinal).Any())
            .ToList();
    }

    public async ValueTask<bool> ExecuteAsync(string id, CancellationToken cancellationToken = default)
    {
        var state = StateOf(id);
        if (!state.CanExecute)
        {
            return false;
        }
        await _handlers[id](cancellationToken).ConfigureAwait(true);
        InvalidateStates();
        return true;
    }

    public void InvalidateStates() => StatesInvalidated?.Invoke(this, EventArgs.Empty);
}

/// <summary>Command bus (section 21.1).</summary>
public interface ICommandBus
{
    /// <summary>Runs the command if it is implemented and can run; returns whether it ran.</summary>
    ValueTask<bool> ExecuteAsync(string id, CancellationToken cancellationToken = default);
}
