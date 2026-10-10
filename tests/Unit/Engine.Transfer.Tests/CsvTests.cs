using System.Globalization;

namespace Eunomia.Engine.Transfer.Tests;

/// <summary>CSV import do lado do cliente (FAM-E07; anexo B §3.3): parser em streaming e INSERT multi-row.</summary>
public sealed class CsvTests
{
    [Fact]
    public void DefaultsDoHeidiSqlComAspasDuplicadasENulls()
    {
        const string csv = "id;name;note\r\n1;\"a;b\";x\r\n2;\"diz \"\"olá\"\"\";\\N\r\n3;NULL;\"NULL\"\r\n4;\"linha\r\nnova\";\r\n";
        var rows = ReadAll(csv, new CsvOptions());

        Assert.Equal(4, rows.Count);
        Same(["1", "a;b", "x"], rows[0]);
        Same(["2", "diz \"olá\"", null], rows[1]);
        Same(["3", null, "NULL"], rows[2]);
        Same(["4", "linha\r\nnova", ""], rows[3]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    public void MesmoResultadoComQualquerBuffer(int buffer)
    {
        const string csv = "h1;h2\r\n\"a\"\"\";b\r\n\"x\r\ny\";\"\"\r\n\r\nz;\"w\"\r\nfim";
        var expected = ReadAll(csv, new CsvOptions(), 4096);
        var actual = ReadAll(csv, new CsvOptions(), Math.Max(16, buffer), chunk: buffer);

        Assert.Equal(expected, actual);
        Assert.Equal(4, actual.Count);
        Same(["fim"], actual[^1]);
    }

    [Fact]
    public void EscapeComBarraInvertidaETerminadoresPersonalizados()
    {
        var options = new CsvOptions
        {
            FieldTerminator = CsvOptions.Unescape("\\t"),
            LineTerminator = CsvOptions.Unescape("\\n"),
            Escaper = "\\",
            IgnoreLines = 0,
        };
        var rows = ReadAll("a\\tb\t\"c\\\"d\"\t\\N\n\\Nx\tsem\\\\barra\t\n", options);

        Same(["a\tb", "c\"d", null], rows[0]);
        Same(["Nx", "sem\\barra", ""], rows[1]);
    }

    [Fact]
    public void RetomaSaltaOsRegistosJaLidos()
    {
        const string csv = "h\r\n1\r\n2\r\n3\r\n4\r\n";
        var reader = new CsvReader(new StringReader(csv), new CsvOptions());
        Same(["1"], reader.Read());
        Same(["2"], reader.Read());
        var checkpoint = reader.RecordsRead;

        var resumed = new CsvReader(new StringReader(csv), new CsvOptions());
        resumed.Skip(checkpoint);

        Assert.Equal(3, checkpoint);
        Same(["3"], resumed.Read());
        Same(["4"], resumed.Read());
        Assert.Null(resumed.Read());
    }

    [Fact]
    public void InsertMultiRowComTiposDaColunaDeDestino()
    {
        CsvTargetColumn[] columns = [new("price", true), new("name", false), new("qty", true)];
        var batcher = new CsvInsertBatcher("loja", "t", columns, CsvDuplicates.Replace, lowPriority: true, localNumbers: new CultureInfo("pt-PT").NumberFormat);

        Assert.Null(batcher.Add(["1 234,5", "O'Neil\\", "7", "a mais"]));
        Assert.Null(batcher.Add(["abc", null]));
        var sql = batcher.Flush();

        Assert.Equal("REPLACE LOW_PRIORITY INTO `loja`.`t` (`price`, `name`, `qty`) VALUES (1234.5, 'O\\'Neil\\\\', 7), ('abc', NULL, NULL)", sql);
        Assert.Null(batcher.Flush());
    }

    [Fact]
    public void InsertFechaPorTamanhoOuPorLinhas()
    {
        var byRows = new CsvInsertBatcher("d", "t", [new CsvTargetColumn("a", true)], CsvDuplicates.InsertIgnore, maxRows: 2);
        var statements = Enumerable.Range(1, 5).Select(i => byRows.Add([i.ToString(CultureInfo.InvariantCulture)])).ToList();
        statements.Add(byRows.Flush());

        Assert.Equal(["INSERT IGNORE INTO `d`.`t` (`a`) VALUES (1), (2)", "INSERT IGNORE INTO `d`.`t` (`a`) VALUES (3), (4)", "INSERT IGNORE INTO `d`.`t` (`a`) VALUES (5)"], statements.OfType<string>());

        var bySize = new CsvInsertBatcher("d", "t", [new CsvTargetColumn("a", false)], CsvDuplicates.Insert, maxBytes: 60);
        var sized = Enumerable.Range(1, 6).Select(_ => bySize.Add([new string('x', 10)])).ToList();
        sized.Add(bySize.Flush());
        sized.RemoveAll(s => s is null);

        Assert.All(sized, s => Assert.True(s!.Length <= 60, s));
        Assert.Equal(6, sized.Sum(s => s!.Split("'xxxxxxxxxx'").Length - 1));
    }

    [Fact]
    public void JournalGuardaELeOEstado()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eunomia-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new TransferJournal(directory);
            var input = Path.Combine(directory, "in.sql");
            Directory.CreateDirectory(directory);
            File.WriteAllText(input, new string('x', 3 * 1024 * 1024));
            var entry = new TransferJournalEntry
            {
                Id = Guid.NewGuid(),
                Kind = TransferJobKind.SqlImport,
                Session = "local",
                Database = "loja",
                Path = input,
                InputSize = 3 * 1024 * 1024,
                InputFingerprint = TransferJournal.Fingerprint(input),
                Offset = 1234,
                CharOffset = 1200,
                Line = 17,
                Delimiter = "//",
                Count = 99,
                Options = new Dictionary<string, string> { ["encoding"] = "utf-8" },
            };
            journal.Save(entry);
            File.WriteAllText(Path.Combine(directory, "job-cortado.json"), "{\"id\":");

            var listed = Assert.Single(journal.List());
            Assert.Equal(entry.Id, listed.Id);
            Assert.Equal(TransferJobKind.SqlImport, listed.Kind);
            Assert.Equal("//", listed.Delimiter);
            Assert.Equal(17, listed.Line);
            Assert.Equal("utf-8", listed.Options["encoding"]);
            Assert.Equal(entry.InputFingerprint, TransferJournal.Fingerprint(input));
            Assert.Contains("\"kind\": \"SqlImport\"", File.ReadAllText(journal.PathOf(entry.Id)), StringComparison.Ordinal);

            File.AppendAllText(input, "y");
            Assert.NotEqual(entry.InputFingerprint, TransferJournal.Fingerprint(input));
            journal.Delete(entry.Id);
            Assert.Empty(journal.List());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Same(string?[] expected, string?[]? actual) =>
        Assert.Equal(Show(expected), actual is null ? "(fim)" : Show(actual));

    private static string Show(string?[] row) => string.Join(" | ", row.Select(v => v ?? "<NULL>"));

    private static List<string?[]> ReadAll(string csv, CsvOptions options, int bufferSize = 4096, int chunk = int.MaxValue)
    {
        var reader = new CsvReader(new ChunkedReader(csv, chunk), options, bufferSize);
        var rows = new List<string?[]>();
        while (reader.Read() is { } row)
        {
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Devolve no máximo <c>chunk</c> caracteres por leitura, como um ficheiro lido aos bocados.</summary>
    private sealed class ChunkedReader(string text, int chunk) : TextReader
    {
        private int _pos;

        public override int Read(char[] buffer, int index, int count)
        {
            var n = Math.Min(Math.Min(count, chunk), text.Length - _pos);
            text.CopyTo(_pos, buffer, index, n);
            _pos += n;
            return n;
        }
    }
}
