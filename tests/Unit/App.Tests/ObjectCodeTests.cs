using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;

namespace Eunomia.App.Tests;

/// <summary>Editor por código de views, rotinas, triggers e events (Fase 13).</summary>
public sealed class ObjectCodeTests
{
    [Theory]
    [InlineData("CREATE ALGORITHM=UNDEFINED DEFINER=`root`@`localhost` SQL SECURITY DEFINER VIEW `v_totals` AS select 1", DbObjectKind.View, "v_totals")]
    [InlineData("CREATE DEFINER=`root`@`%` PROCEDURE `shop`.`p close`()\nBEGIN SELECT 1; END", DbObjectKind.Procedure, "p close")]
    [InlineData("create or replace function f_total(a INT) RETURNS INT RETURN a", DbObjectKind.Function, "f_total")]
    [InlineData("-- comment\nCREATE DEFINER='app'@'10.0.%' TRIGGER `trg` BEFORE INSERT ON t FOR EACH ROW SET NEW.a = 1", DbObjectKind.Trigger, "trg")]
    [InlineData("/* x */ CREATE EVENT IF NOT EXISTS `e``q` ON SCHEDULE EVERY 1 DAY DO SELECT 1", DbObjectKind.Event, "e`q")]
    [InlineData("CREATE AGGREGATE FUNCTION agg() RETURNS INT BEGIN RETURN 1; END", DbObjectKind.Function, "agg")]
    public void NomeETipoDoCreate(string code, DbObjectKind kind, string name)
    {
        Assert.Equal((kind, name), ObjectCode.Parse(code));
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("CREATE TABLE t (a INT)")]
    [InlineData("")]
    public void SemCreateDeObjetoPorCodigo(string code)
    {
        Assert.Null(ObjectCode.Parse(code));
    }

    [Theory]
    [InlineData(DbObjectKind.View)]
    [InlineData(DbObjectKind.Procedure)]
    [InlineData(DbObjectKind.Function)]
    [InlineData(DbObjectKind.Trigger)]
    [InlineData(DbObjectKind.Event)]
    public void ModelosSaoCreatesValidos(DbObjectKind kind)
    {
        var template = ObjectCode.Template(kind, new MySqlDialect(MySqlConnectionFactory.ParseIdentity("10.4.32-MariaDB", null)), "orders");

        Assert.Equal(kind, ObjectCode.Parse(template)?.Kind);
        Assert.True(ObjectCode.IsCodeObject(kind));
        Assert.False(ObjectCode.IsCodeObject(DbObjectKind.Table));
    }

    [Theory]
    [InlineData("CREATE VIEW v AS SELECT 1", "CREATE OR REPLACE VIEW v AS SELECT 1")]
    [InlineData("-- x\ncreate ALGORITHM=MERGE VIEW v AS SELECT 1", "-- x\ncreate OR REPLACE ALGORITHM=MERGE VIEW v AS SELECT 1")]
    [InlineData("CREATE OR REPLACE VIEW v AS SELECT 1", "CREATE OR REPLACE VIEW v AS SELECT 1")]
    [InlineData("CREATE  or  replace PROCEDURE p() SELECT 1", "CREATE  or  replace PROCEDURE p() SELECT 1")]
    [InlineData("SELECT 1", "SELECT 1")]
    public void CreateOrReplace(string code, string expected)
    {
        Assert.Equal(expected, ObjectCode.WithOrReplace(code));
    }

    [Theory]
    [InlineData("CREATE DEFINER=`root`@`%` PROCEDURE `shop`.`p`()\nBEGIN SELECT 1; END", "CREATE DEFINER=`root`@`%` PROCEDURE `shop`.`tmp`()\nBEGIN SELECT 1; END")]
    [InlineData("/* c */ CREATE FUNCTION f() RETURNS INT RETURN 1", "/* c */ CREATE FUNCTION `tmp`() RETURNS INT RETURN 1")]
    public void OutroNomeParaValidar(string code, string expected)
    {
        Assert.Equal(expected, ObjectCode.WithName(code, "`tmp`"));
    }
}
