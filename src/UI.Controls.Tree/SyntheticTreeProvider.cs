namespace Eunomia.UI.Controls.Tree;

/// <summary>
/// Synthetic, deterministic tree for the spike: by default 5 coloured sessions × 20 databases × 100 objects
/// (90 tables, 5 views, 3 procedures, 2 functions) = 10,000 objects. Children are "requested" with a
/// configurable latency, like a <c>SHOW TABLE STATUS</c>.
/// </summary>
public sealed class SyntheticTreeProvider : ITreeDataProvider
{
    /// <summary>Session colours seen in the screenshots (section 5.6), in ARGB.</summary>
    public static readonly uint[] SessionColors = [0xFF006400, 0xFFDC143C, 0xFFF03333, 0xFF76845C, 0xFF9D850B];

    private static readonly string[] Prefixes = ["b2b", "ecm", "prd", "std", "app", "sys", "tmp", "log", "phpbb"];

    private static readonly string[] Words =
    [
        "budgets", "products", "orders", "stores", "categories", "users", "roles", "price_tables", "status",
        "documents", "lines", "discount", "account", "config", "import", "sessions", "web_roles", "addresses",
    ];

    private readonly int _databasesPerSession;
    private readonly int _objectsPerDatabase;
    private readonly TimeSpan _latency;
    private readonly bool _groupByType;

    public SyntheticTreeProvider(
        int databasesPerSession = 20,
        int objectsPerDatabase = 100,
        TimeSpan latency = default,
        bool groupByType = false)
    {
        _databasesPerSession = databasesPerSession;
        _objectsPerDatabase = objectsPerDatabase;
        _latency = latency;
        _groupByType = groupByType;
    }

    /// <summary>Number of calls to <see cref="LoadChildrenAsync"/> (for lazy loading tests).</summary>
    public int LoadCount { get; private set; }

    public static IReadOnlyList<TreeNode> CreateSessions(int count = 5)
    {
        var sessions = new TreeNode[count];
        for (var s = 0; s < count; s++)
        {
            sessions[s] = new TreeNode(TreeNodeKind.Session, s == 0 ? "localhost" : $"server-{s:D2}")
            {
                SessionColor = SessionColors[s % SessionColors.Length],
            };
        }
        return sessions;
    }

    public async Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(TreeNode parent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parent);
        LoadCount++;
        if (_latency > TimeSpan.Zero)
        {
            await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        }
        return parent.Kind switch
        {
            TreeNodeKind.Session => Databases(parent),
            TreeNodeKind.Database => _groupByType ? Groups(parent) : Objects(parent, parent, null),
            TreeNodeKind.Group => Objects(parent.Parent!, parent, GroupKind(parent.Name)),
            _ => [],
        };
    }

    private TreeNode[] Databases(TreeNode session)
    {
        var sessionIndex = Hash(session.Name, 0) % 1000;
        var result = new TreeNode[_databasesPerSession];
        for (var d = 0; d < result.Length; d++)
        {
            var name = d switch
            {
                0 => "information_schema",
                1 => "mysql",
                _ => $"{Prefixes[(int)((sessionIndex + (ulong)d) % (ulong)Prefixes.Length)]}_db{d:D2}",
            };
            result[d] = new TreeNode(TreeNodeKind.Database, name, session);
        }
        return result;
    }

    private static TreeNode[] Groups(TreeNode database) =>
    [
        new(TreeNodeKind.Group, "Tables", database),
        new(TreeNodeKind.Group, "Views", database),
        new(TreeNodeKind.Group, "Procedures", database),
        new(TreeNodeKind.Group, "Functions", database),
    ];

    private static TreeNodeKind? GroupKind(string group) => group switch
    {
        "Tables" => TreeNodeKind.Table,
        "Views" => TreeNodeKind.View,
        "Procedures" => TreeNodeKind.Procedure,
        _ => TreeNodeKind.Function,
    };

    private List<TreeNode> Objects(TreeNode database, TreeNode parent, TreeNodeKind? only)
    {
        var list = new List<TreeNode>(_objectsPerDatabase);
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < _objectsPerDatabase; i++)
        {
            var kind = (i % 100) switch
            {
                < 90 => TreeNodeKind.Table,
                < 95 => TreeNodeKind.View,
                < 98 => TreeNodeKind.Procedure,
                _ => TreeNodeKind.Function,
            };
            if (only is not null && kind != only)
            {
                continue;
            }
            var h = Hash(database.Session!.Name + "/" + database.Name, i);
            var name = $"{Prefixes[(int)(h % (ulong)Prefixes.Length)]}_{Words[(int)((h >> 8) % (ulong)Words.Length)]}";
            if (kind == TreeNodeKind.View)
            {
                name = "v_" + name;
            }
            else if (kind is TreeNodeKind.Procedure or TreeNodeKind.Function)
            {
                name = (kind == TreeNodeKind.Procedure ? "sp_" : "fn_") + name;
            }
            if (!used.Add(name))
            {
                name = $"{name}_{i}";
                used.Add(name);
            }
            list.Add(new TreeNode(kind, name, parent)
            {
                SizeBytes = kind == TreeNodeKind.Table ? (long)(16 * 1024 + ((h >> 20) % (64UL * 1024 * 1024))) : null,
            });
        }
        // The tree sorts objects by name with natural comparison (section 4.2).
        list.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        return list;
    }

    private static ulong Hash(string text, int i)
    {
        // FNV-1a: stable across runs (string.GetHashCode is not).
        var h = 14695981039346656037UL;
        foreach (var c in text)
        {
            h = (h ^ c) * 1099511628211UL;
        }
        h = (h ^ (ulong)i) * 1099511628211UL;
        h ^= h >> 29;
        return h * 0xBF58476D1CE4E5B9UL;
    }
}

/// <summary>Natural comparison: digit runs compared as numbers, the rest case-insensitive.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }
        if (x is null)
        {
            return -1;
        }
        if (y is null)
        {
            return 1;
        }
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }
                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length)
                {
                    return a.Length.CompareTo(b.Length);
                }
                var cmp = a.SequenceCompareTo(b);
                if (cmp != 0)
                {
                    return cmp;
                }
                continue;
            }
            var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (c != 0)
            {
                return c;
            }
            i++;
            j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
