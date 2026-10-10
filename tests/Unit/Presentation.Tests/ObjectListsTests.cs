using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Metadata;
using Eunomia.Presentation.Objects;

namespace Eunomia.Presentation.Tests;

/// <summary>Host and Database tab lists (section 4.9; appendix C §1.6, §1.8).</summary>
public sealed class ObjectListsTests
{
    private static DbObjectInfo Obj(string name, DbObjectKind kind, long? data = null, DateTime? updated = null, IReadOnlyDictionary<string, string?>? status = null) =>
        new(new DbObjectRef("shop", "shop", name, kind), DataBytes: data, IndexBytes: data is null ? null : 0, Updated: updated) { Status = status };

    [Fact]
    public void UncachedDatabasesStayEmptyAndCachedOnesCountByKind()
    {
        var objects = new DatabaseObjects("shop",
        [
            Obj("a", DbObjectKind.Table, 100, new DateTime(2026, 1, 2)),
            Obj("b", DbObjectKind.Table, 50, new DateTime(2026, 3, 4)),
            Obj("v", DbObjectKind.View),
            Obj("p", DbObjectKind.Procedure),
        ], ObjectDetail.Statistics, DateTimeOffset.UtcNow);

        var rows = HostDatabases.Build(["shop", "other"], db => db == "shop" ? objects : null, new Dictionary<string, string> { ["shop"] = "utf8mb4_general_ci" });

        var shop = rows[0];
        Assert.Equal(150, shop.Size);
        Assert.Equal(4, shop.Items);
        Assert.Equal(new DateTime(2026, 3, 4), shop.LastModified);
        Assert.Equal(2, shop.CountOf(DbObjectKind.Table));
        Assert.Equal(1, shop.CountOf(DbObjectKind.View));
        Assert.Equal(0, shop.CountOf(DbObjectKind.Event));
        Assert.Equal("utf8mb4_general_ci", shop.Collation);
        var other = rows[1];
        Assert.Null(other.Items);
        Assert.Null(other.CountOf(DbObjectKind.Table));
        Assert.Null(other.Size);
    }

    [Fact]
    public void WithoutStatisticsTheDatabaseSizeStaysEmpty()
    {
        var objects = new DatabaseObjects("shop", [Obj("a", DbObjectKind.Table)], ObjectDetail.Names, DateTimeOffset.UtcNow);

        var row = HostDatabases.Build(["shop"], _ => objects, null)[0];

        Assert.Null(row.Size);
        Assert.Equal(1, row.Items);
    }

    [Fact]
    public void DatabaseTabStatus()
    {
        IReadOnlyList<DbObjectInfo> objects = [Obj("a", DbObjectKind.Table), Obj("b", DbObjectKind.Table), Obj("v", DbObjectKind.View)];

        Assert.Equal("shop: 3 objects (2 tables, 1 views)", DatabaseObjectList.StatusText("shop", objects));
        Assert.Equal("shop: 0 objects", DatabaseObjectList.StatusText("shop", []));
    }

    [Fact]
    public void DatabaseTabColumns()
    {
        Assert.Equal(["Name", "Rows", "Size", "Created", "Updated", "Engine", "Comment", "Type"],
            DatabaseObjectList.Columns.Where(c => c.VisibleByDefault).Select(c => c.Header));
        Assert.Equal(11, DatabaseObjectList.Columns.Count(c => !c.VisibleByDefault));

        var table = Obj("a", DbObjectKind.Table, 100, status: new Dictionary<string, string?> { ["Auto_increment"] = "42", ["Row_format"] = "Dynamic" });
        Assert.Equal(100L, DatabaseObjectList.Value(table, 2));
        Assert.Equal("Table", DatabaseObjectList.Value(table, 7));
        Assert.Equal(42L, DatabaseObjectList.Value(table, 14));
        Assert.Equal("Dynamic", DatabaseObjectList.Value(table, 9));
        // Views and routines show neither rows nor size.
        Assert.Null(DatabaseObjectList.Value(Obj("v", DbObjectKind.View, 10), 2));
        Assert.Equal("Procedure", DatabaseObjectList.Value(Obj("p", DbObjectKind.Procedure), 7));
    }

    [Fact]
    public void KillDoesNotKillTheSessionsOwnConnections()
    {
        Assert.Equal([7L, 9L], ProcessList.ToKill([7, 3, 9, 7], [3, null]));
    }

    [Fact]
    public void VariablesJoinSessionAndGlobalAndHighlightDifferences()
    {
        var rows = HostVariables.Build(
            [new("sort_buffer_size", "1048576", true), new("autocommit", "ON", true), new("session_only", "x", true)],
            [new("autocommit", "ON", false), new("sort_buffer_size", "262144", false), new("global_only", "y", false)]);

        Assert.Equal(["autocommit", "global_only", "session_only", "sort_buffer_size"], rows.Select(r => r.Name));
        Assert.False(rows[0].Differs);
        Assert.Null(rows[1].Session);
        Assert.False(rows[1].Differs);
        Assert.True(rows[3].Differs);
    }

    [Fact]
    public void StatusWithAveragesOverUptime()
    {
        var rows = HostStatus.Build([new("Uptime", "7200", false), new("Questions", "3600", false), new("Ssl_cipher", "", false)]);

        Assert.Equal(1800, rows[1].PerHour);
        Assert.Equal(0.5, rows[1].PerSecond);
        Assert.Null(rows[2].PerHour);
    }

    [Fact]
    public void CommandStatisticsWithPercentageInDescendingOrder()
    {
        var rows = CommandStatistics.Build(
        [
            new("Uptime", "3600", false), new("Com_select", "30", false), new("Com_alter_table", "10", false),
            new("Com_insert", "60", false), new("Compression", "OFF", false), new("Questions", "500", false),
        ]);

        Assert.Equal(["Insert", "Select", "Alter table"], rows.Select(r => r.Command));
        Assert.Equal(60, rows[0].Percentage);
        Assert.Equal(10, rows[2].Percentage);
        Assert.Equal(30, rows[1].PerHour);
    }
}
