namespace Eunomia.Engine.Execution;

/// <summary>Effect of a statement on the connection's transaction (section 14.5).</summary>
public enum TransactionEffect
{
    /// <summary>A read or a statement with no effect on the transaction.</summary>
    None,

    /// <summary>May change data (INSERT, UPDATE, DELETE, REPLACE, LOAD DATA, CALL).</summary>
    Change,

    /// <summary><c>BEGIN</c>, <c>START TRANSACTION</c>, <c>XA START</c>.</summary>
    Begin,

    /// <summary><c>COMMIT</c>, <c>XA COMMIT</c>.</summary>
    Commit,

    /// <summary><c>ROLLBACK</c> (not to a savepoint), <c>XA ROLLBACK</c>.</summary>
    Rollback,

    /// <summary><c>COMMIT AND CHAIN</c> / <c>ROLLBACK AND CHAIN</c>: ends and opens another one right away.</summary>
    EndAndChain,

    /// <summary>DDL and the like: the server commits implicitly before running it.</summary>
    ImplicitCommit,

    /// <summary><c>SET autocommit = 0</c>.</summary>
    AutocommitOff,

    /// <summary><c>SET autocommit = 1</c> (commits whatever is open).</summary>
    AutocommitOn,
}

/// <summary>
/// Transaction state of the primary connection (section 14.5: "the status bar shows an open transaction"), tracked from the
/// statements the connection sends, because the driver does not expose <c>SERVER_STATUS_IN_TRANS</c>. Counts as open after
/// <c>BEGIN</c>/<c>START TRANSACTION</c>, or after any statement with <c>autocommit = 0</c>, until <c>COMMIT</c>,
/// <c>ROLLBACK</c>, DDL (implicit commit) or the connection dropping. It is an approximation: a <c>COMMIT</c> inside a routine
/// is not seen; that is why the UI speaks of a "probably" open transaction only where it matters (closing, reconnecting).
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

    /// <summary>The state changed (may come from any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>There is an open transaction (explicit, or implicit with <c>autocommit = 0</c>).</summary>
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

    /// <summary>The open transaction already had statements that change data (lost on a rollback).</summary>
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

    /// <summary>Updates the state with a statement the connection sent (several, if the text has more than one).</summary>
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
    /// The connection dropped or was reopened, or the server rolled the transaction back (deadlock): nothing stays open and
    /// autocommit goes back to the server default (on). Returns whether there was a transaction with (lost) changes.
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

    /// <summary>Deadlock (the server rolls back the whole transaction) or another server-side rollback; autocommit stays.</summary>
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
                // A BEGIN with an open transaction commits it first.
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

    /// <summary>Effect of a statement, from its first words (leading comments and whitespace ignored).</summary>
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
                // BEGIN NOT ATOMIC is a MariaDB compound block, not a transaction.
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
                // CTE followed by UPDATE/DELETE (MySQL 8): the word is searched for in the text.
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

    /// <summary><c>SET [SESSION|LOCAL|@@[session.]]autocommit = 0|1|ON|OFF</c>, also among other assignments.</summary>
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

    /// <summary>First words of the statement, without comments (<c>/*! … */</c> ones count as code).</summary>
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
                    // Executable comment: skips the "/*!" and the version and reads what is inside.
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
                // Another symbol (quotes, operators): the rest does not count for the first words.
                break;
            }
        }
        return words;
    }
}
