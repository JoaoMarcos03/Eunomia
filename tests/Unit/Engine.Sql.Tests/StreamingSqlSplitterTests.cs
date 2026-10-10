using System.Text;
using Eunomia.Engine.Sql.Splitting;

namespace Eunomia.Engine.Sql.Tests;

/// <summary>Splitter em streaming: o mesmo resultado do <see cref="SqlSplitter"/> com qualquer tamanho de pedaço, e retoma.</summary>
public sealed class StreamingSqlSplitterTests
{
    private static readonly string[] Scripts =
    [
        "SELECT 1; SELECT 2;\nSELECT 3",
        "INSERT INTO t VALUES ('a;b', \"c;d\", `e;f`);\nSELECT 'it''s', 'x\\'y;', \"q\"\"r;\";\n",
        "-- comentário; com ponto e vírgula\nSELECT 1; # outro;\nSELECT 2 /* bloco ; \n continua; */ ;\n--\nSELECT 3;--x\nSELECT 4",
        "SET @a = 1;\nDELIMITER //\nCREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\n  SELECT 2;\nEND//\nDELIMITER ;\nCALL p();\n",
        "  DELIMITER $$\r\nSELECT 1; SELECT 2$$\r\n\tdelimiter ;\r\nSELECT 3;\r\n",
        "SELECT 1; DELIMITER //\nSELECT 2;",
        "/*!40101 SET NAMES utf8 */;\n/* só comentário */;\n;;\nSELECT '\n;\n';\n",
        "DELIMITER\nSELECT 1;\nDELIMITERX //\nSELECT 2;\nDELIMITER '\nSELECT 3;",
        "SELECT 'aberta; sem fim",
        "SELECT 1 /* aberto ; ",
        "SELECT a-b; SELECT a--b;\nSELECT c- -d; SELECT 1 --",
        "SELECT '\\\\'; SELECT '\\\n;'; SELECT `a\\`;",
        "DELIMITER ;;\nSELECT 1;;\nSELECT 2;\nSELECT 3;;\nDELIMITER ;\nSELECT 4;",
        "SELECT 1;\n\n\n  SELECT 2  ;  \n\t-- fim\n",
        "DELIMITER //",
        "SELECT 'ação', \"€\"; SELECT `ü`;",
    ];

    public static TheoryData<int, int> Cases()
    {
        var data = new TheoryData<int, int>();
        for (var s = 0; s < Scripts.Length; s++)
        {
            foreach (var chunk in new[] { 1, 2, 3, 5, 8, 64, 100_000 })
            {
                data.Add(s, chunk);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void IgualAoSplitterComQualquerTamanhoDePedaco(int script, int chunk)
    {
        var text = Scripts[script];
        var expected = SqlSplitter.Split(text);
        var actual = SplitInChunks(text, chunk, new StreamingSqlSplitter());

        Assert.Equal(expected.Select(e => (e.Text, (long)e.Start, (long)e.End, (long)e.Line, e.Delimiter)), actual.Select(a => (a.Text, a.Start, a.End, a.Line, a.Delimiter)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RetomaEmCadaPontoDaASequenciaQueFalta(int script, int chunk)
    {
        var text = Scripts[script];
        var all = SplitInChunks(text, 100_000, new StreamingSqlSplitter());
        for (var i = 0; i < all.Count; i++)
        {
            var next = all[i].Next;
            var rest = SplitInChunks(text[(int)next.Offset..], chunk, new StreamingSqlSplitter(next));
            Assert.Equal(all.Skip(i + 1), rest);
        }
    }

    [Fact]
    public void DelimiterMantemSeNaFronteiraDoPedaco()
    {
        // O bug do HeidiSQL (anexo B §4.3): o DELIMITER perde-se quando o bloco de 20 MB acaba dentro dele.
        var body = new StringBuilder("DELIMITER //\n");
        for (var i = 0; i < 200; i++)
        {
            body.Append("CREATE PROCEDURE p").Append(i).Append("() BEGIN SELECT 1; SELECT 2; END//\n");
        }
        var statements = SplitInChunks(body.ToString(), 4096, new StreamingSqlSplitter());

        Assert.Equal(200, statements.Count);
        Assert.All(statements, s => Assert.Equal("//", s.Delimiter));
        Assert.All(statements, s => Assert.EndsWith("END", s.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void RetomaDepoisDeUmaLinhaDelimiterUsaONovoDelimitador()
    {
        const string text = "SELECT 1\nDELIMITER //\nSELECT 2; SELECT 3//\n";
        var all = SplitInChunks(text, 100_000, new StreamingSqlSplitter());

        Assert.Equal(["SELECT 1", "SELECT 2; SELECT 3"], all.Select(s => s.Text));
        Assert.Equal(";", all[0].Delimiter);
        Assert.Equal("//", all[0].Next.Delimiter);
        Assert.Equal(text.IndexOf("\nSELECT 2", StringComparison.Ordinal), all[0].Next.Offset);
        Assert.Equal(2, all[0].Next.Line);
    }

    [Fact]
    public void SoAInstrucaoPorAcabarFicaEmMemoria()
    {
        var splitter = new StreamingSqlSplitter();
        var output = new List<StreamedStatement>();
        var row = "INSERT INTO t VALUES (1, 'abcdefghij');\n";
        for (var i = 0; i < 10_000; i++)
        {
            splitter.Append(row, output);
        }
        splitter.Append("SELECT 'por acabar", output);

        Assert.Equal(10_000, output.Count);
        Assert.Equal("SELECT 'por acabar".Length + 1, splitter.PendingLength);
        Assert.Equal((long)row.Length * 9_999, output[^1].Start);
    }

    private static List<StreamedStatement> SplitInChunks(string text, int chunk, StreamingSqlSplitter splitter)
    {
        var output = new List<StreamedStatement>();
        for (var i = 0; i < text.Length; i += chunk)
        {
            splitter.Append(text.AsSpan(i, Math.Min(chunk, text.Length - i)), output);
        }
        splitter.Complete(output);
        return output;
    }
}
