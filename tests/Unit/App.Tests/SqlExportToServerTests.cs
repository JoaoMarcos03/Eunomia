using Eunomia.App.Transfer;
using Eunomia.Engine.Transfer;

namespace Eunomia.App.Tests;

/// <summary>
/// Export to a database or a server: the target can never be an exported database on the same server, where "Delete +
/// insert" or "Drop" would destroy the source (AGENTS.md section 5).
/// </summary>
public sealed class SqlExportToServerTests
{
    private static SqlExportRequest Request(SqlExportTarget target, string? targetDatabase, Guid? session = null) => new()
    {
        Dump = new SqlDumpOptions { DropTables = true, CreateTables = true, Data = DumpDataMode.DeleteInsert },
        Databases = [new SqlExportDatabase("shop", []), new SqlExportDatabase("Stats", [])],
        Target = target,
        TargetDatabase = targetDatabase,
        TargetSession = session,
    };

    [Theory]
    [InlineData("shop")]
    [InlineData("SHOP")]
    [InlineData("stats")]
    public void AnExportedDatabaseIsNeverTheTargetOnTheSameServer(string target)
    {
        Assert.Contains("is one of the exported databases", SqlExportToServerJob.Validate(Request(SqlExportTarget.Database, target), sameServer: true), StringComparison.Ordinal);
        Assert.Contains("is one of the exported databases", SqlExportToServerJob.Validate(Request(SqlExportTarget.Server, target, Guid.NewGuid()), sameServer: true), StringComparison.Ordinal);
    }

    [Fact]
    public void SameNamesAreOnlyAllowedOnAnotherServer()
    {
        var request = Request(SqlExportTarget.Server, null, Guid.NewGuid());

        Assert.NotNull(SqlExportToServerJob.Validate(request, sameServer: true));
        Assert.Null(SqlExportToServerJob.Validate(request, sameServer: false));
        Assert.Null(SqlExportToServerJob.Validate(Request(SqlExportTarget.Server, "shop", Guid.NewGuid()), sameServer: false));
    }

    [Fact]
    public void TargetsMustBeChosen()
    {
        Assert.Equal("Please select a target database.", SqlExportToServerJob.Validate(Request(SqlExportTarget.Database, null), sameServer: true));
        Assert.Equal("Please select a target database.", SqlExportToServerJob.Validate(Request(SqlExportTarget.Database, string.Empty), sameServer: true));
        Assert.Equal("Please select a target server.", SqlExportToServerJob.Validate(Request(SqlExportTarget.Server, "copy"), sameServer: false));
        Assert.Null(SqlExportToServerJob.Validate(Request(SqlExportTarget.Database, "shop_copy"), sameServer: true));
    }
}
