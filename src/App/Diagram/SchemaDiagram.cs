using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eunomia.App.Settings;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;

namespace Eunomia.App.Diagram;

/// <summary>Column in the diagram (SV-01).</summary>
public sealed record DiagramColumn(string Name, string Type, bool PrimaryKey, bool Unique, bool ForeignKey, bool Nullable, TypeCategory Category);

public sealed record DiagramTable(string Name, IReadOnlyList<DiagramColumn> Columns);

/// <summary>FK relation: from the table with the FK to the referenced one; 1:1 when the FK columns are a key (SV-03).</summary>
public sealed record DiagramRelation(string Name, string Table, IReadOnlyList<string> Columns, string ReferencedTable, IReadOnlyList<string> ReferencedColumns, bool OneToOne);

/// <summary>
/// Schema Visualizer model of a database (section 17): tables, columns with PK/UNIQUE/FK/NULL and the type category,
/// FK relations within the database, and a schema hash (names, types, keys and relations) for layout persistence.
/// </summary>
public sealed record SchemaDiagram(string Database, IReadOnlyList<DiagramTable> Tables, IReadOnlyList<DiagramRelation> Relations, string Hash)
{
    public static SchemaDiagram From(string database, IEnumerable<TableDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(definitions);
        var all = definitions.OrderBy(d => d.Ref.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var names = new HashSet<string>(all.Select(d => d.Ref.Name), StringComparer.Ordinal);
        var tables = new List<DiagramTable>();
        var relations = new List<DiagramRelation>();
        foreach (var table in all)
        {
            var primary = table.Indexes.FirstOrDefault(i => i.Kind == IndexKind.Primary)?.Columns ?? [];
            var uniques = table.Indexes.Where(i => i.Kind == IndexKind.Unique).ToList();
            var foreign = table.ForeignKeys.SelectMany(f => f.Columns).ToHashSet(StringComparer.OrdinalIgnoreCase);
            tables.Add(new DiagramTable(table.Ref.Name, [.. table.Columns.Select(c => new DiagramColumn(
                c.Name,
                c.NativeType,
                primary.Contains(c.Name, StringComparer.OrdinalIgnoreCase),
                uniques.Any(u => u.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)),
                foreign.Contains(c.Name),
                c.IsNullable,
                c.Type.Category))]));
            foreach (var fk in table.ForeignKeys)
            {
                // Only the relations within the database: the others have no node.
                if (!string.Equals(fk.ReferencedTable.Schema, database, StringComparison.Ordinal) || !names.Contains(fk.ReferencedTable.Name))
                {
                    continue;
                }
                var columns = new HashSet<string>(fk.Columns, StringComparer.OrdinalIgnoreCase);
                var oneToOne = (primary.Count > 0 && columns.SetEquals(primary)) || uniques.Any(u => columns.SetEquals(u.Columns));
                relations.Add(new DiagramRelation(fk.Name, table.Ref.Name, fk.Columns, fk.ReferencedTable.Name, fk.ReferencedColumns, oneToOne));
            }
        }
        return new SchemaDiagram(database, tables, relations, HashOf(tables, relations));
    }

    private static string HashOf(IReadOnlyList<DiagramTable> tables, IReadOnlyList<DiagramRelation> relations)
    {
        var sb = new StringBuilder();
        foreach (var t in tables)
        {
            sb.Append('T').Append(t.Name).Append('\n');
            foreach (var c in t.Columns)
            {
                sb.Append(CultureInfo.InvariantCulture, $"C{c.Name}|{c.Type}|{c.PrimaryKey}|{c.Unique}|{c.ForeignKey}|{c.Nullable}\n");
            }
        }
        foreach (var r in relations.OrderBy(r => r.Table, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            sb.Append('R').Append(r.Table).Append('|').Append(r.Name).Append('|').Append(r.ReferencedTable).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

/// <summary>Position and visibility of a table in the diagram; without <see cref="Placed"/>, there is no position yet.</summary>
public readonly record struct DiagramNodeState(double X, double Y, bool Hidden, bool Placed = true);

/// <summary>
/// Saved layout of a diagram (SV-12): per session and database, with the hash of the schema it was made for, the
/// positions, the hidden tables and the view. Stored in <c>app.db</c> (state of the database's empty "table").
/// </summary>
public sealed record DiagramLayoutState(string Hash, IReadOnlyDictionary<string, DiagramNodeState> Nodes, double Zoom, double OffsetX, double OffsetY)
{
    private const string StateName = "diagram.layout";

    public static DiagramLayoutState? Load(IAppDatabase database, string session, string schema)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.GetTableState(new TableKey(session, schema, string.Empty), StateName) is not { Length: > 0 } json)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var nodes = new Dictionary<string, DiagramNodeState>(StringComparer.Ordinal);
            foreach (var node in root.GetProperty("nodes").EnumerateObject())
            {
                var values = node.Value;
                nodes[node.Name] = new DiagramNodeState(values[0].GetDouble(), values[1].GetDouble(), values.GetArrayLength() > 2 && values[2].GetBoolean(),
                    values.GetArrayLength() <= 3 || values[3].GetBoolean());
            }
            return new DiagramLayoutState(root.GetProperty("hash").GetString() ?? string.Empty, nodes,
                root.GetProperty("zoom").GetDouble(), root.GetProperty("offsetX").GetDouble(), root.GetProperty("offsetY").GetDouble());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            // Unreadable layout: it is made again.
            return null;
        }
    }

    public void Save(IAppDatabase database, string session, string schema)
    {
        ArgumentNullException.ThrowIfNull(database);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("hash", Hash);
            writer.WriteNumber("zoom", Zoom);
            writer.WriteNumber("offsetX", OffsetX);
            writer.WriteNumber("offsetY", OffsetY);
            writer.WriteStartObject("nodes");
            foreach (var (name, state) in Nodes.OrderBy(n => n.Key, StringComparer.Ordinal))
            {
                writer.WriteStartArray(name);
                writer.WriteNumberValue(Math.Round(state.X, 1));
                writer.WriteNumberValue(Math.Round(state.Y, 1));
                writer.WriteBooleanValue(state.Hidden);
                writer.WriteBooleanValue(state.Placed);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        database.SetTableState(new TableKey(session, schema, string.Empty), StateName, Encoding.UTF8.GetString(stream.ToArray()));
    }
}
