using Eunomia.App.Sessions;
using Eunomia.Engine.Execution;

namespace Eunomia.App.Tests;

/// <summary>Per-session "Log queries to file" (phase 14): the user's DDL and DML, with passwords masked.</summary>
public sealed class QueryFileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eunomia-qlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class Inner : ISessionLog
    {
        public List<(SessionLogCategory Category, string Message)> Lines { get; } = [];

        public void Write(string session, SessionLogCategory category, string message) => Lines.Add((category, message));
    }

    [Fact]
    public void SoDdlEDmlDoUtilizadorComPasswordsMascaradas()
    {
        var inner = new Inner();
        var path = Path.Combine(_dir, "sub", "prod.sql");
        var log = new QueryFileLog(inner, path, ddl: true, dml: true);

        log.Write("prod", SessionLogCategory.UserSql, "SELECT * FROM t");
        log.Write("prod", SessionLogCategory.UserSql, "UPDATE t SET a = 1 WHERE id = 2 LIMIT 1");
        log.Write("prod", SessionLogCategory.InternalSql, "DELETE FROM interno");
        log.Write("prod", SessionLogCategory.UserSql, "CREATE USER 'u'@'%' IDENTIFIED BY 'segredo'");
        log.Write("prod", SessionLogCategory.UserSql, "ALTER TABLE t ADD b INT");

        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("UPDATE t SET a = 1 WHERE id = 2 LIMIT 1;", lines[0], StringComparison.Ordinal);
        Assert.Contains("[prod]", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("segredo", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("ALTER TABLE t ADD b INT;", lines[2], StringComparison.Ordinal);
        // The normal SQL log gets everything.
        Assert.Equal(5, inner.Lines.Count);
    }

    [Fact]
    public void SoDmlEFalhaDeEscritaNaoParaNada()
    {
        var inner = new Inner();
        var log = new QueryFileLog(inner, Path.Combine(_dir, "dml.sql"), ddl: false, dml: true);
        log.Write("s", SessionLogCategory.UserSql, "DROP TABLE t");
        log.Write("s", SessionLogCategory.UserSql, "DELETE FROM t");
        Assert.Single(File.ReadAllLines(Path.Combine(_dir, "dml.sql")));

        // Impossible path: one error line in the SQL log, only once, and the statements keep going through.
        Directory.CreateDirectory(_dir);
        var blocked = Path.Combine(_dir, "pasta");
        Directory.CreateDirectory(blocked);
        var broken = new QueryFileLog(inner, blocked, ddl: true, dml: true);
        broken.Write("s", SessionLogCategory.UserSql, "DELETE FROM t");
        broken.Write("s", SessionLogCategory.UserSql, "DELETE FROM t");
        Assert.Single(inner.Lines, l => l.Category == SessionLogCategory.Error);
    }
}
