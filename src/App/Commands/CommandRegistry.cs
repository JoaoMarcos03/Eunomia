namespace Eunomia.App.Commands;

/// <summary>
/// Contextos de foco onde um comando está ativo (secção 21.2). <see cref="Global"/> vale em todos. A UI marca os
/// controlos com o contexto; o foco atual dá uma lista do contexto mais interior para o mais exterior.
/// </summary>
public static class CommandContexts
{
    public const string Global = "global";
    public const string Tree = "tree";
    public const string Grid = "grid";
    public const string QueryEditor = "editor";

    /// <summary>Editor WHERE do Data tab (Apply filter com F9, Clear com Ctrl+W).</summary>
    public const string FilterPanel = "filterPanel";

    public const string TreeFilter = "treeFilter";
    public const string Log = "log";

    /// <summary>Listas do Host tab (Del = Kill process).</summary>
    public const string HostList = "hostList";

    /// <summary>Lista de objetos do Database tab (partilha o <c>popupDB</c> com a árvore).</summary>
    public const string ObjectList = "objectList";

    /// <summary>Painel de helpers do Query tab.</summary>
    public const string Helpers = "helpers";
}

/// <summary>Tipo de item: ação simples, toggle (check) ou opção de um grupo de rádio.</summary>
public enum CommandKind
{
    Action,
    Toggle,
    Radio,
}

/// <summary>
/// Uma ação do utilizador (secção 21.2): id estável, caption, categoria, ícone, atalho por omissão, contextos e
/// predicado. <see cref="HeidiSqlAction"/> guarda o nome da action do HeidiSQL quando é conhecido (para importar
/// atalhos e testar que o inventário está completo). Sem handler, o comando aparece desativado, com
/// <see cref="AvailableIn"/> no tooltip, para manter a geografia dos menus.
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
    /// <summary>Texto de ajuda (hint do HeidiSQL), mostrado em tooltips e no Preferences › Shortcuts.</summary>
    public string? Hint { get; init; }

    public CommandKind Kind { get; init; }

    /// <summary>Grupo de rádio (<see cref="CommandKind.Radio"/>).</summary>
    public string? RadioGroup { get; init; }

    /// <summary>Escondido nos menus (actions com <c>Visible=False</c> no HeidiSQL), mas presente no inventário.</summary>
    public bool Hidden { get; init; }

    public IReadOnlyList<string> ActiveIn => Contexts is { Count: > 0 } ? Contexts : [CommandContexts.Global];

    public bool IsGlobal => ActiveIn.Contains(CommandContexts.Global);
}

/// <summary>Dois comandos com o mesmo atalho que podem estar ativos ao mesmo tempo.</summary>
public sealed record ShortcutConflict(KeyChord Shortcut, string FirstId, string SecondId, string Context);

/// <summary>Estado de um comando no contexto atual, para menus, toolbar e tooltips.</summary>
public sealed record CommandState(CommandDescriptor Command, bool IsImplemented, bool CanExecute, bool IsChecked, string? Tooltip, string Caption);

/// <summary>
/// Registo de comandos (secção 21.2): menus, toolbar, context menus e keymap são gerados daqui. Os atalhos
/// resolvem-se pelo contexto de foco, como no HeidiSQL: um comando do contexto focado ganha a um global
/// (F9 = Run no editor, Apply filter no filter panel), e um comando que não pode correr não consome o atalho
/// (Esc = Cancel editing só com alterações pendentes, senão Cancel running operation). Atalhos redefinidos pelo
/// utilizador (Preferences › Shortcuts, FAM-K01) substituem o atalho por omissão. Não é thread-safe: usar na UI thread.
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

    /// <summary>Algo mudou (handler, atalho, estado): menus e toolbar devem reler <see cref="StateOf"/>.</summary>
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

    /// <summary>Comando pelo nome da action do HeidiSQL (<c>actExecuteQuery</c>).</summary>
    public CommandDescriptor? FindByHeidiSqlAction(string action) =>
        All.FirstOrDefault(c => string.Equals(c.HeidiSqlAction, action, StringComparison.OrdinalIgnoreCase));

    /// <summary>Liga o comando a um handler; sem handler fica desativado ("disponível na versão X").</summary>
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

    /// <summary>Handler síncrono (atalho para a maioria dos comandos da UI).</summary>
    public void SetHandler(string id, Action handler, Func<bool>? canExecute = null) =>
        SetHandler(id, _ => { handler(); return ValueTask.CompletedTask; }, canExecute);

    /// <summary>Estado de check de um toggle ou rádio.</summary>
    public void SetChecked(string id, Func<bool> isChecked)
    {
        Get(id);
        _checked[id] = isChecked;
    }

    /// <summary>
    /// Caption calculada quando o menu abre (Quick Filter com o SQL real, "Select top N rows"); <c>null</c> usa a do
    /// catálogo.
    /// </summary>
    public void SetCaption(string id, Func<string?> caption)
    {
        Get(id);
        _captions[id] = caption;
    }

    /// <summary>Atalho efetivo: o redefinido pelo utilizador, se existir, senão o por omissão.</summary>
    public KeyChord? ShortcutOf(string id) =>
        _overrides.TryGetValue(id, out var custom) ? custom : Get(id).DefaultShortcut;

    /// <summary>Redefine o atalho (<c>null</c> remove-o). Para voltar ao por omissão: <see cref="ResetShortcut"/>.</summary>
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

    /// <summary>Atalhos redefinidos, no formato das settings (id → "Ctrl+X", ou vazio quando removido).</summary>
    public Dictionary<string, string> ExportShortcutOverrides() =>
        _overrides.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty, StringComparer.Ordinal);

    /// <summary>
    /// Aplica atalhos das settings. Ids desconhecidos (comando removido numa versão nova) e textos inválidos são
    /// ignorados e devolvidos, para o log.
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
    /// Comando para um atalho no contexto de foco: primeiro os que estão ativos nesse contexto, depois os globais.
    /// Devolve <c>null</c> se nenhum corresponder. Não olha para <c>CanExecute</c>: ver <see cref="ResolveExecutable"/>.
    /// </summary>
    public CommandDescriptor? Resolve(KeyChord shortcut, string focusContext) =>
        Candidates(shortcut, [focusContext]).FirstOrDefault();

    /// <summary>
    /// Comando que o atalho executa agora: o primeiro que pode correr, procurando nos contextos de foco (do mais
    /// interior para o mais exterior) e depois nos globais. Comandos desativados não consomem o atalho.
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
    /// Conflitos que o Preferences tem de mostrar (FAM-K01): mesmo atalho em dois comandos ativos no mesmo
    /// contexto, ou ambos globais. Um comando de contexto com o mesmo atalho de um global não é conflito:
    /// ganha o do contexto.
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

    /// <summary>Comandos que já usam <paramref name="shortcut"/> num contexto partilhado com <paramref name="id"/>.</summary>
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

/// <summary>Barramento de comandos (secção 21.1).</summary>
public interface ICommandBus
{
    /// <summary>Executa se o comando estiver implementado e puder correr; devolve se correu.</summary>
    ValueTask<bool> ExecuteAsync(string id, CancellationToken cancellationToken = default);
}
