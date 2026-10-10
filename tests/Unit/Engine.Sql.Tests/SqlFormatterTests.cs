using Eunomia.Engine.Sql.Formatting;

namespace Eunomia.Engine.Sql.Tests;

/// <summary>Reformat SQL (Ctrl+F8): formatter interno sobre o lexer.</summary>
public sealed class SqlFormatterTests
{
    private static string F(string sql) => SqlFormatter.Format(sql).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void ClausulasEmLinhasEAndOrIndentados()
    {
        Assert.Equal(
            "SELECT a, COUNT(*) AS n\nFROM t\nWHERE x=1\n\tAND y BETWEEN 1 AND 2\n\tOR z IS NULL\nGROUP BY a\nHAVING n > 1\nORDER BY a DESC\nLIMIT 10",
            F("select a, count(*) as n from t where x=1 and y between 1 and 2 or z is null group by a having n > 1 order by a desc limit 10"));
    }

    [Fact]
    public void JoinsSubqueriesEParentesesSimples()
    {
        Assert.Equal(
            "SELECT *\nFROM a\nLEFT OUTER JOIN b ON b.id = a.id\nINNER JOIN (\n\tSELECT id, EXTRACT(YEAR FROM d) AS y\n\tFROM c\n\tWHERE IF(p AND q, 1, 0) = 1) x ON x.id = a.id",
            F("SELECT * FROM a left outer join b on b.id = a.id inner join (select id, EXTRACT(year from d) as y from c where if(p and q, 1, 0) = 1) x on x.id = a.id"));
    }

    [Fact]
    public void StringsComentariosEInstrucoesSeparadas()
    {
        Assert.Equal(
            "-- head\nSELECT 'from  where', `select`\nFROM t;\n\nUPDATE t\nSET a = 1 /* keep  this */\nWHERE b = 'x\ny';",
            F("-- head\nselect 'from  where', `select` from t;\nupdate t set a = 1 /* keep  this */ where b = 'x\ny';"));
    }

    [Fact]
    public void InsertComValuesEOnDuplicateKeyUpdate()
    {
        Assert.Equal(
            "INSERT INTO t (a, b)\nVALUES (1, 'x')\nON DUPLICATE KEY UPDATE b = VALUES(b)",
            F("insert into t (a, b) values (1, 'x') on duplicate key update b = values(b)"));
    }

    [Fact]
    public void EspacosDentroDeParentesesEAntesDeVirgulas()
    {
        Assert.Equal("SELECT (a, b), IFNULL(c, 0)\nFROM t", F("select ( a , b ) ,  ifnull( c , 0 ) from t"));
    }

    [Fact]
    public void AcoesDasChavesEstrangeirasNaoPartemALinha()
    {
        Assert.Equal(
            "ALTER TABLE t ADD FOREIGN KEY (a) REFERENCES p (id) ON DELETE CASCADE ON UPDATE SET NULL",
            F("alter table t add foreign key (a) references p (id) on delete cascade on update set null"));
        Assert.Equal("CREATE TABLE x (a INT);\n\nDROP TABLE y;\n\nTRUNCATE z", F("create table x (a int); drop table y; truncate z"));
    }

    [Fact]
    public void CharacterSetNaoPartaALinha()
    {
        Assert.Equal("ALTER TABLE t CONVERT TO CHARACTER SET utf8mb4", F("alter table t convert to character set utf8mb4"));
    }
}
