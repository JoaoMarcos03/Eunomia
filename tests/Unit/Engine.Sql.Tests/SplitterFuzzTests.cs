using System.Text;
using Eunomia.Engine.Sql.Lexing;
using Eunomia.Engine.Sql.Splitting;

namespace Eunomia.Engine.Sql.Tests;

/// <summary>
/// Deterministic fuzzing of the splitter (phase 14): random scripts made of hard pieces (quotes with escapes,
/// comments of every kind, <c>DELIMITER</c>, semicolons inside strings, unfinished text, Unicode outside the
/// BMP). Run SQL file reads in 1 MB blocks and resumes midway: the split must not depend on where the blocks fall or on
/// where it resumes, otherwise a broken statement would go to the server.
/// </summary>
public sealed class SplitterFuzzTests
{
    private static readonly string[] Pieces =
    [
        "SELECT 1", "INSERT INTO t VALUES (1, 'a')", ";", ";", " ", "\n", "\r\n", "\t",
        "'a;b'", "'it''s'", "'x\\'y'", "'\\\\'", "\"d;q\"", "\"say \\\"hi\\\"\"", "`c;d`", "`we``ird`",
        "-- c;m\n", "--x", "# h;\n", "/* b; */", "/*! 40101 SET x=1 */", "/* sem fim",
        "DELIMITER //\n", "DELIMITER ;\n", "delimiter $$\n", "//", "$$",
        "BEGIN SELECT 1; END", "CREATE PROCEDURE p() BEGIN SELECT ';'; END", "'sem fim", "ação", "😀", "\\",
    ];

    private static string RandomScript(Random random)
    {
        var sb = new StringBuilder();
        var count = random.Next(1, 40);
        for (var i = 0; i < count; i++)
        {
            sb.Append(Pieces[random.Next(Pieces.Length)]);
        }
        return sb.ToString();
    }

    private static List<StreamedStatement> Stream(string text, Random? chunks, SplitterPosition? start = null)
    {
        var splitter = start is { } s ? new StreamingSqlSplitter(s) : new StreamingSqlSplitter();
        var output = new List<StreamedStatement>();
        var at = start is { } p ? (int)p.Offset : 0;
        while (at < text.Length)
        {
            var size = chunks is null ? text.Length - at : Math.Min(text.Length - at, chunks.Next(1, 65));
            splitter.Append(text.AsSpan(at, size), output);
            at += size;
        }
        splitter.Complete(output);
        return output;
    }

    [Fact]
    public void PedacosAleatoriosNaoMudamADivisao()
    {
        var random = new Random(20261009);
        for (var run = 0; run < 2000; run++)
        {
            var text = RandomScript(random);
            var whole = Stream(text, chunks: null);
            var pieces = Stream(text, new Random(run));
            Assert.Equal(whole.Select(s => (s.Text, s.Start, s.End, s.Line, s.Delimiter)), pieces.Select(s => (s.Text, s.Start, s.End, s.Line, s.Delimiter)));

            // The editor's splitter gives the same statements (Run SQL file and the query tabs send the same thing).
            Assert.Equal(SqlSplitter.Split(text).Select(s => s.Text), whole.Select(s => s.Text));

            // Positions within the text, in order, and the statement is there.
            long previous = 0;
            foreach (var s in whole)
            {
                Assert.InRange(s.Start, previous, text.Length);
                Assert.InRange(s.End, s.Start, text.Length);
                Assert.Equal(s.Text, text[(int)s.Start..(int)s.End]);
                Assert.NotEqual(string.Empty, s.Text);
                previous = s.End;
            }
        }
    }

    [Fact]
    public void RetomarAMeioDaAsMesmasInstrucoesSeguintes()
    {
        var random = new Random(9102026);
        for (var run = 0; run < 1000; run++)
        {
            var text = RandomScript(random);
            var whole = Stream(text, chunks: null);
            for (var k = 0; k < whole.Count; k++)
            {
                // Resumes from the point saved in the journal after statement k (with the delimiter in effect).
                var resumed = Stream(text, new Random(run + k), whole[k].Next);
                Assert.Equal(whole.Skip(k + 1).Select(s => (s.Text, s.Start, s.Delimiter)), resumed.Select(s => (s.Text, s.Start, s.Delimiter)));
            }
        }
    }

    [Fact]
    public void LexerNuncaRebentaETokensFicamNaLinha()
    {
        var random = new Random(42);
        for (var run = 0; run < 2000; run++)
        {
            var text = RandomScript(random);
            var lines = new List<int>();
            foreach (var line in text.AsSpan().EnumerateLines())
            {
                lines.Add(line.Length);
            }
            var tokens = MySqlLexer.Tokenize(text);
            // Tokens within their own line, in order and without overlaps.
            var last = (Line: -1, End: 0);
            foreach (var (line, token) in tokens)
            {
                Assert.InRange(line, 0, lines.Count - 1);
                Assert.True(token.Length > 0);
                Assert.True(token.Start + token.Length <= lines[line]);
                if (line == last.Line)
                {
                    Assert.True(token.Start >= last.End);
                }
                last = (line, token.Start + token.Length);
            }
        }
    }
}
