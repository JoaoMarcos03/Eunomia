using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Eunomia.Engine.Results;
using Eunomia.Engine.Results.Synthetic;
using Eunomia.Engine.Sql.Lexing;

namespace Eunomia.Bench.Micro;

/// <summary>Custo de produzir um bloco de 1.024 × 60 e de formatar colunas para display (secções 11 e 15.2).</summary>
[MemoryDiagnoser]
public class RowBlockBenchmarks
{
    private readonly SyntheticRowSource _source = new();
    private readonly DisplayFormatter _formatter = new(CultureInfo.InvariantCulture);
    private RowBlock _block = null!;

    [GlobalSetup]
    public void Setup() => _block = _source.BuildBlock(100);

    [Benchmark(Description = "BuildBlock 1024x60")]
    public RowBlock BuildBlock() => _source.BuildBlock(100);

    [Benchmark(Description = "FormatColumn INT")]
    public string[] FormatInt() => _formatter.FormatColumn(_block, 1);

    [Benchmark(Description = "FormatColumn DECIMAL")]
    public string[] FormatDecimal() => _formatter.FormatColumn(_block, 3);

    [Benchmark(Description = "FormatColumn TEXT")]
    public string[] FormatText() => _formatter.FormatColumn(_block, 6);
}

/// <summary>Lexer MySQL por linha (secção 16.2: microssegundos por linha).</summary>
[MemoryDiagnoser]
public class LexerBenchmarks
{
    private string[] _lines = [];
    private readonly List<SqlToken> _tokens = new(64);

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 1000; i++)
        {
            sb.Append("SELECT `id`, name, COUNT(*) AS n FROM `db`.`t").Append(i).Append("` WHERE x > ").Append(i)
              .Append(" AND s = 'it''s' -- comentário\n");
        }
        _lines = sb.ToString().Split('\n');
    }

    [Benchmark(Description = "1000 linhas")]
    public LexerState Tokenize()
    {
        var state = LexerState.Normal;
        foreach (var line in _lines)
        {
            _tokens.Clear();
            state = MySqlLexer.TokenizeLine(line, state, _tokens);
        }
        return state;
    }
}
