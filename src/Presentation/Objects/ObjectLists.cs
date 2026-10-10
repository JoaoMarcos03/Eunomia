using System.Globalization;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Metadata;

namespace Eunomia.Presentation.Objects;

/// <summary>Column of a Host or Database tab list: header, default width and initial visibility.</summary>
public sealed record ListColumn(string Header, double Width, bool VisibleByDefault = true, bool Numeric = false, bool Bars = false);

/// <summary>A database in the Host tab's Databases list (section 4.9). Values come from the cache; without a cache, they stay empty.</summary>
public sealed record HostDatabaseRow(
    string Name,
    long? Size,
    int? Items,
    DateTime? LastModified,
    IReadOnlyDictionary<DbObjectKind, int>? Counts,
    string? Collation)
{
    public int? CountOf(DbObjectKind kind) => Counts is null ? null : Counts.GetValueOrDefault(kind);
}

/// <summary>Host tab Databases list (appendix C §1.8): columns and rows from the metadata cache.</summary>
public static class HostDatabases
{
    /// <summary>Kinds in the order of the Tables, Views, Functions, Procedures, Triggers, Events columns.</summary>
    public static readonly IReadOnlyList<DbObjectKind> CountKinds =
        [DbObjectKind.Table, DbObjectKind.View, DbObjectKind.Function, DbObjectKind.Procedure, DbObjectKind.Trigger, DbObjectKind.Event];

    /// <summary>Bars on numeric columns 1, 2 and 4–9.</summary>
    public static readonly IReadOnlyList<ListColumn> Columns =
    [
        new("Database", 150),
        new("Size", 80, Numeric: true, Bars: true),
        new("Items", 70, Numeric: true, Bars: true),
        new("Last modification", 130),
        new("Tables", 60, Numeric: true, Bars: true),
        new("Views", 60, Numeric: true, Bars: true),
        new("Functions", 70, Numeric: true, Bars: true),
        new("Procedures", 75, Numeric: true, Bars: true),
        new("Triggers", 60, Numeric: true, Bars: true),
        new("Events", 60, Numeric: true, Bars: true),
        new("Default collation", 120),
    ];

    public static IReadOnlyList<HostDatabaseRow> Build(IReadOnlyList<string> databases, Func<string, DatabaseObjects?> cached, IReadOnlyDictionary<string, string>? collations)
    {
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(cached);
        var rows = new List<HostDatabaseRow>(databases.Count);
        foreach (var db in databases)
        {
            var collation = collations?.GetValueOrDefault(db);
            if (cached(db) is not { } objects)
            {
                rows.Add(new HostDatabaseRow(db, null, null, null, null, collation));
                continue;
            }
            var counts = CountKinds.ToDictionary(k => k, _ => 0);
            DateTime? last = null;
            long size = 0;
            foreach (var o in objects.Objects)
            {
                var kind = o.Ref.Kind == DbObjectKind.MaterializedView ? DbObjectKind.View : o.Ref.Kind;
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                if (o.Updated is { } updated && (last is null || updated > last))
                {
                    last = updated;
                }
                if (kind == DbObjectKind.Table)
                {
                    size += o.SizeBytes ?? 0;
                }
            }
            rows.Add(new HostDatabaseRow(db, objects.HasStatistics ? size : null, objects.Objects.Count, last, counts, collation));
        }
        return rows;
    }
}

/// <summary>Database tab object list (<c>ListTables</c>, appendix C §1.8).</summary>
public static class DatabaseObjectList
{
    /// <summary>Visible by default: Name, Rows, Size, Created, Updated, Engine, Comment, Type; the next 11 hidden.</summary>
    public static readonly IReadOnlyList<ListColumn> Columns =
    [
        new("Name", 120),
        new("Rows", 70, Numeric: true),
        new("Size", 70, Numeric: true, Bars: true),
        new("Created", 120),
        new("Updated", 120),
        new("Engine", 70),
        new("Comment", 100),
        new("Type", 80),
        new("Version", 60, false, true),
        new("Row format", 80, false),
        new("Avg row length", 90, false, true),
        new("Max data length", 100, false, true),
        new("Index length", 90, false, true),
        new("Data free", 80, false, true),
        new("Auto increment", 90, false, true),
        new("Check time", 120, false),
        new("Collation", 120, false),
        new("Checksum", 80, false),
        new("Create options", 120, false),
    ];

    /// <summary><c>SHOW TABLE STATUS</c> field of each hidden column, in <see cref="Columns"/> order from "Version" on.</summary>
    private static readonly string[] StatusFields =
        ["Version", "Row_format", "Avg_row_length", "Max_data_length", "Index_length", "Data_free", "Auto_increment", "Check_time", "Collation", "Checksum", "Create_options"];

    public static string TypeName(DbObjectKind kind) => kind switch
    {
        DbObjectKind.View or DbObjectKind.MaterializedView => "View",
        DbObjectKind.Procedure => "Procedure",
        DbObjectKind.Function => "Function",
        DbObjectKind.Trigger => "Trigger",
        DbObjectKind.Event => "Event",
        _ => "Table",
    };

    /// <summary>Raw value of a cell (number, date or text), to format and sort.</summary>
    public static object? Value(DbObjectInfo obj, int column)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var isTable = obj.Ref.Kind == DbObjectKind.Table;
        switch (column)
        {
            case 0:
                return obj.Ref.Name;
            case 1:
                return isTable ? obj.RowsEstimate : null;
            case 2:
                return isTable ? obj.SizeBytes : null;
            case 3:
                return obj.Created;
            case 4:
                return obj.Updated;
            case 5:
                return obj.Engine;
            case 6:
                return obj.Comment;
            case 7:
                return TypeName(obj.Ref.Kind);
            default:
                var field = StatusFields[column - 8];
                var text = obj.Status?.GetValueOrDefault(field);
                return Columns[column].Numeric && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : text;
        }
    }

    /// <summary>Status bar panel 0 (appendix C §1.6): <c>db: 12 objects (10 tables, 2 views)</c>.</summary>
    public static string StatusText(string database, IReadOnlyList<DbObjectInfo> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var parts = new List<string>();
        foreach (var (kind, word) in new[]
                 {
                     (DbObjectKind.Table, "tables"), (DbObjectKind.View, "views"), (DbObjectKind.Procedure, "procedures"),
                     (DbObjectKind.Function, "functions"), (DbObjectKind.Trigger, "triggers"), (DbObjectKind.Event, "events"),
                 })
        {
            var count = objects.Count(o => (o.Ref.Kind == DbObjectKind.MaterializedView ? DbObjectKind.View : o.Ref.Kind) == kind);
            if (count > 0)
            {
                parts.Add(count.ToString(CultureInfo.CurrentCulture) + " " + word);
            }
        }
        var text = $"{database}: {objects.Count.ToString(CultureInfo.CurrentCulture)} objects";
        return parts.Count > 0 ? text + " (" + string.Join(", ", parts) + ")" : text;
    }
}

/// <summary>Host tab Processes list: id (sorted descending), User, Host, DB, Command, Time (with bars), State, Info.</summary>
public static class ProcessList
{
    public static readonly IReadOnlyList<ListColumn> Columns =
    [
        new("id", 70, Numeric: true),
        new("User", 90),
        new("Host", 120),
        new("DB", 90),
        new("Command", 80),
        new("Time", 60, Numeric: true, Bars: true),
        new("State", 120),
        new("Info", 182),
    ];

    /// <summary>"Kill N Process(es)?": the ids to kill, without the session's own.</summary>
    public static IReadOnlyList<long> ToKill(IEnumerable<long> selected, IEnumerable<long?> ownConnections)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(ownConnections);
        var own = ownConnections.Where(c => c is not null).Select(c => c!.Value).ToHashSet();
        return selected.Where(id => !own.Contains(id)).Distinct().ToList();
    }
}

/// <summary>A variable in Host tab › Variables (section 4.9): session value and global value.</summary>
public sealed record HostVariableRow(string Name, string? Session, string? Global)
{
    /// <summary>Session value differs from the global one: the Session cell is highlighted (BlanchedAlmond in HeidiSQL).</summary>
    public bool Differs => Session is not null && Global is not null && !string.Equals(Session, Global, StringComparison.Ordinal);
}

/// <summary>Host tab › Variables: <c>SHOW SESSION VARIABLES</c> and <c>SHOW GLOBAL VARIABLES</c> side by side, by name.</summary>
public static class HostVariables
{
    public static readonly IReadOnlyList<ListColumn> Columns = [new("Variable", 160), new("Session", 200), new("Global", 312)];

    public static IReadOnlyList<HostVariableRow> Build(IReadOnlyList<ServerVariable> session, IReadOnlyList<ServerVariable> global)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(global);
        var sessionValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in session)
        {
            sessionValues.TryAdd(v.Name, v.Value);
        }
        var globalValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in global)
        {
            globalValues.TryAdd(v.Name, v.Value);
        }
        return sessionValues.Keys.Union(globalValues.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(n => new HostVariableRow(n, sessionValues.GetValueOrDefault(n), globalValues.GetValueOrDefault(n)))
            .ToList();
    }
}

/// <summary>A row of Host tab › Status: value and per-hour and per-second averages since the server started.</summary>
public sealed record HostStatusRow(string Name, string Value, double? PerHour, double? PerSecond);

/// <summary>Host tab › Status (<c>SHOW GLOBAL STATUS</c>): the averages use the server's <c>Uptime</c>.</summary>
public static class HostStatus
{
    public static readonly IReadOnlyList<ListColumn> Columns =
        [new("Variable", 160), new("Value", 312), new("Avg per hour", 100, Numeric: true), new("Avg per second", 100, Numeric: true)];

    public static IReadOnlyList<HostStatusRow> Build(IReadOnlyList<ServerVariable> status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var uptime = Uptime(status);
        return status.Select(v =>
        {
            var numeric = double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && uptime > 0;
            return new HostStatusRow(v.Name, v.Value, numeric ? value / (uptime / 3600) : null, numeric ? value / uptime : null);
        }).ToList();
    }

    internal static double Uptime(IReadOnlyList<ServerVariable> status) =>
        status.FirstOrDefault(v => string.Equals(v.Name, "Uptime", StringComparison.OrdinalIgnoreCase)) is { } u
        && double.TryParse(u.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
}

/// <summary>A row of Host tab › Command-Statistics: a <c>Com_*</c> counter.</summary>
public sealed record CommandStatisticsRow(string Command, long Count, double? PerHour, double? PerSecond, double Percentage);

/// <summary>
/// Host tab › Command-Statistics: the <c>Com_*</c> counters of <c>SHOW GLOBAL STATUS</c> ("Com_alter_table" → "Alter
/// table"), with averages over <c>Uptime</c> and the percentage of the total of all counters.
/// </summary>
public static class CommandStatistics
{
    public static readonly IReadOnlyList<ListColumn> Columns =
    [
        new("Command-type", 120), new("Total count", 100, Numeric: true), new("Average per hour", 100, Numeric: true),
        new("Average per second", 100, Numeric: true), new("Percentage", 252, Numeric: true, Bars: true),
    ];

    public static IReadOnlyList<CommandStatisticsRow> Build(IReadOnlyList<ServerVariable> status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var uptime = HostStatus.Uptime(status);
        var counters = status
            .Where(v => v.Name.StartsWith("Com_", StringComparison.OrdinalIgnoreCase) && v.Name.Length > 4)
            .Select(v => (Name: v.Name[4..].Replace('_', ' '), Ok: long.TryParse(v.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n), Count: n))
            .Where(c => c.Ok)
            .ToList();
        var total = counters.Sum(c => (double)c.Count);
        return counters.Select(c => new CommandStatisticsRow(
                char.ToUpperInvariant(c.Name[0]) + c.Name[1..],
                c.Count,
                uptime > 0 ? c.Count / (uptime / 3600) : null,
                uptime > 0 ? c.Count / uptime : null,
                total > 0 ? c.Count * 100 / total : 0))
            .OrderByDescending(c => c.Count)
            .ToList();
    }
}
