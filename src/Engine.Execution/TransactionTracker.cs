namespace Eunomia.Engine.Execution;

/// <summary>Efeito de uma instrução na transação da ligação (secção 14.5).</summary>
public enum TransactionEffect
{
    /// <summary>Leitura ou instrução sem efeito na transação.</summary>
    None,

    /// <summary>Pode mudar dados (INSERT, UPDATE, DELETE, REPLACE, LOAD DATA, CALL).</summary>
    Change,

    /// <summary><c>BEGIN</c>, <c>START TRANSACTION</c>, <c>XA START</c>.</summary>
    Begin,

    /// <summary><c>COMMIT</c>, <c>XA COMMIT</c>.</summary>
    Commit,

    /// <summary><c>ROLLBACK</c> (não para um savepoint), <c>XA ROLLBACK</c>.</summary>
    Rollback,

    /// <summary><c>COMMIT AND CHAIN</c> / <c>ROLLBACK AND CHAIN</c>: acaba e abre outra logo.</summary>
    EndAndChain,

    /// <summary>DDL e afins: o servidor faz commit implícito antes de correr.</summary>
    ImplicitCommit,

    /// <summary><c>SET autocommit = 0</c>.</summary>
    AutocommitOff,

    /// <summary><c>SET autocommit = 1</c> (faz commit do que estiver aberto).</summary>
    AutocommitOn,
}

/// <summary>
/// Estado da transação da ligação primária (secção 14.5: "a status bar mostra transação aberta"), seguido pelas
/// instruções que a ligação envia, porque o driver não expõe o <c>SERVER_STATUS_IN_TRANS</c>. Conta como aberta depois de
/// <c>BEGIN</c>/<c>START TRANSACTION</c>, ou de qualquer instrução com <c>autocommit = 0</c>, até ao <c>COMMIT</c>,
/// <c>ROLLBACK</c>, DDL (commit implícito) ou à ligação cair. É uma aproximação: um <c>COMMIT</c> dentro de uma rotina não
/// se vê; por isso a UI fala em transação "provavelmente" aberta só onde isso importa (fechar, religar).
/// </summary>
public sealed class TransactionTracker
{
    private static readonly HashSet<string> ImplicitCommitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALTER", "RENAME", "TRUNCATE", "LOCK", "UNLOCK", "GRANT", "REVOKE", "ANALYZE", "OPTIMIZE", "REPAIR", "FLUSH", "RESET",
        "INSTALL", "UNINSTALL", "CACHE", "CHANGE",
    };

    private static readonly HashSet<string> ChangeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "REPLACE", "CALL", "MERGE",
    };

    private readonly Lock _gate = new();
    private bool _explicit;
    private bool _autocommitOff;
    private bool _active;
    private bool _changes;

    /// <summary>O estado mudou (pode vir de qualquer thread).</summary>
    public event EventHandler? Changed;

    /// <summary>Há uma transação aberta (explícita, ou implícita com <c>autocommit = 0</c>).</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _explicit || _active;
            }
        }
    }

    /// <summary>A transação aberta já teve instruções que mudam dados (perdem-se num rollback).</summary>
    public bool HasChanges
    {
        get
        {
            lock (_gate)
            {
                return (_explicit || _active) && _changes;
            }
        }
    }

    public bool AutocommitOff
    {
        get
        {
            lock (_gate)
            {
                return _autocommitOff;
            }
        }
    }

    /// <summary>Atualiza o estado com uma instrução que a ligação enviou (várias, se o texto tiver mais do que uma).</summary>
    public void Observe(IEnumerable<string> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        var changed = false;
        lock (_gate)
        {
            foreach (var statement in statements)
            {
                changed |= Apply(Classify(statement));
            }
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// A ligação caiu ou foi reaberta, ou o servidor desfez a transação (deadlock): nada fica aberto e o autocommit volta
    /// ao valor por omissão do servidor (ligado). Devolve se havia uma transação com alterações (perdidas).
    /// </summary>
    public bool Reset()
    {
        bool lost;
        bool changed;
        lock (_gate)
        {
            lost = (_explicit || _active) && _changes;
            changed = _explicit || _active || _autocommitOff || _changes;
            _explicit = _active = _autocommitOff = _changes = false;
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return lost;
    }

    /// <summary>Deadlock (o servidor desfaz a transação inteira) ou outro rollback do lado do servidor; o autocommit fica.</summary>
    public bool RolledBackByServer()
    {
        bool lost;
        lock (_gate)
        {
            lost = (_explicit || _active) && _changes;
            _explicit = _active = _changes = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return lost;
    }

    private bool Apply(TransactionEffect effect)
    {
        var before = (_explicit, _active, _autocommitOff, _changes);
        switch (effect)
        {
            case TransactionEffect.Begin:
                // Um BEGIN com uma transação aberta faz commit dela primeiro.
                _explicit = true;
                _active = false;
                _changes = false;
                break;
            case TransactionEffect.Commit or TransactionEffect.Rollback or TransactionEffect.ImplicitCommit:
                _explicit = false;
                _active = false;
                _changes = false;
                break;
            case TransactionEffect.EndAndChain:
                _active = true;
                _changes = false;
                break;
            case TransactionEffect.AutocommitOff:
                _autocommitOff = true;
                break;
            case TransactionEffect.AutocommitOn:
                _autocommitOff = false;
                _explicit = false;
                _active = false;
                _changes = false;
                break;
            case TransactionEffect.Change:
                if (_explicit || _autocommitOff)
                {
                    _active = true;
                    _changes = true;
                }
                break;
            default:
                if (_autocommitOff)
                {
                    _active = true;
                }
                break;
        }
        return before != (_explicit, _active, _autocommitOff, _changes);
    }

    /// <summary>Efeito de uma instrução, pelas primeiras palavras (comentários e espaços à frente ignorados).</summary>
    public static TransactionEffect Classify(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var words = Words(statement, 6);
        if (words.Count == 0)
        {
            return TransactionEffect.None;
        }
        var first = words[0].ToUpperInvariant();
        var second = words.Count > 1 ? words[1].ToUpperInvariant() : string.Empty;
        string Rest() => string.Join(' ', words.Skip(1)).ToUpperInvariant();
        switch (first)
        {
            case "BEGIN":
                // BEGIN NOT ATOMIC é um bloco composto do MariaDB, não uma transação.
                return second is "" or "WORK" ? TransactionEffect.Begin : TransactionEffect.None;
            case "START":
                return second == "TRANSACTION" ? TransactionEffect.Begin : TransactionEffect.ImplicitCommit;
            case "XA":
                return second switch
                {
                    "START" or "BEGIN" => TransactionEffect.Begin,
                    "COMMIT" => TransactionEffect.Commit,
                    "ROLLBACK" => TransactionEffect.Rollback,
                    _ => TransactionEffect.None,
                };
            case "COMMIT":
                return Rest().Contains("AND CHAIN", StringComparison.Ordinal) && !Rest().Contains("AND NO CHAIN", StringComparison.Ordinal)
                    ? TransactionEffect.EndAndChain
                    : TransactionEffect.Commit;
            case "ROLLBACK":
                if (second == "TO" || (second == "WORK" && words.Count > 2 && words[2].Equals("TO", StringComparison.OrdinalIgnoreCase)))
                {
                    return TransactionEffect.None;
                }
                return Rest().Contains("AND CHAIN", StringComparison.Ordinal) && !Rest().Contains("AND NO CHAIN", StringComparison.Ordinal)
                    ? TransactionEffect.EndAndChain
                    : TransactionEffect.Rollback;
            case "SET":
                return Autocommit(statement) ?? TransactionEffect.None;
            case "CREATE" or "DROP":
                return second == "TEMPORARY" ? TransactionEffect.None : TransactionEffect.ImplicitCommit;
            case "LOAD":
                return second is "DATA" or "XML" ? TransactionEffect.Change : TransactionEffect.ImplicitCommit;
            case "WITH":
                // CTE seguida de UPDATE/DELETE (MySQL 8): procura-se a palavra no texto.
                var upper = statement.ToUpperInvariant();
                return upper.Contains("UPDATE ", StringComparison.Ordinal) || upper.Contains("DELETE ", StringComparison.Ordinal) || upper.Contains("INSERT ", StringComparison.Ordinal)
                    ? TransactionEffect.Change
                    : TransactionEffect.None;
            default:
                if (ChangeWords.Contains(first))
                {
                    return TransactionEffect.Change;
                }
                return ImplicitCommitWords.Contains(first) ? TransactionEffect.ImplicitCommit : TransactionEffect.None;
        }
    }

    /// <summary><c>SET [SESSION|LOCAL|@@[session.]]autocommit = 0|1|ON|OFF</c>, também no meio de outras atribuições.</summary>
    private static TransactionEffect? Autocommit(string statement)
    {
        var text = statement.ToUpperInvariant();
        var at = text.IndexOf("AUTOCOMMIT", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }
        var i = at + "AUTOCOMMIT".Length;
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ':'))
        {
            i++;
        }
        if (i >= text.Length || text[i] != '=')
        {
            return null;
        }
        i++;
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '\''))
        {
            i++;
        }
        var end = i;
        while (end < text.Length && char.IsLetterOrDigit(text[end]))
        {
            end++;
        }
        return text[i..end] switch
        {
            "0" or "OFF" or "FALSE" => TransactionEffect.AutocommitOff,
            "1" or "ON" or "TRUE" => TransactionEffect.AutocommitOn,
            _ => null,
        };
    }

    /// <summary>Primeiras palavras da instrução, sem comentários (os <c>/*! … */</c> contam como código).</summary>
    private static List<string> Words(string text, int max)
    {
        var words = new List<string>(max);
        var i = 0;
        while (i < text.Length && words.Count < max)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '(' || c == ';')
            {
                i++;
            }
            else if (c == '#' || (c == '-' && i + 1 < text.Length && text[i + 1] == '-'))
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end + 1;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                if (i + 2 < text.Length && text[i + 2] == '!')
                {
                    // Comentário executável: salta o "/*!" e a versão e lê o que está dentro.
                    i += 3;
                    while (i < text.Length && char.IsAsciiDigit(text[i]))
                    {
                        i++;
                    }
                }
                else
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                }
            }
            else if (char.IsLetter(c) || c == '_' || c == '@')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@' or '.'))
                {
                    i++;
                }
                words.Add(text[start..i]);
            }
            else
            {
                // Outro símbolo (aspas, operadores): o resto não conta para as primeiras palavras.
                break;
            }
        }
        return words;
    }
}
