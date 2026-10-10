using System.Text;
using Eunomia.Engine.Sql.Lexing;
using Eunomia.Engine.Sql.Splitting;

namespace Eunomia.Engine.Sql.Tests;

/// <summary>
/// Fuzzing determinístico do splitter (Fase 14): scripts aleatórios feitos de pedaços difíceis (aspas com escapes,
/// comentários de todos os tipos, <c>DELIMITER</c>, ponto e vírgula dentro de strings, texto por acabar, Unicode fora do
/// BMP). O Run SQL file lê em blocos de 1 MB e retoma a meio: a divisão não pode depender de onde caem os blocos nem de
/// onde se retoma, senão uma instrução partida ia para o servidor.
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

            // O splitter do editor dá as mesmas instruções (o Run SQL file e as query tabs enviam o mesmo).
            Assert.Equal(SqlSplitter.Split(text).Select(s => s.Text), whole.Select(s => s.Text));

            // Posições dentro do texto, por ordem, e a instrução está lá.
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
                // Retoma pelo ponto gravado no journal depois da instrução k (com o delimitador em vigor).
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
            // Tokens dentro da linha deles, por ordem e sem sobreposições.
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
