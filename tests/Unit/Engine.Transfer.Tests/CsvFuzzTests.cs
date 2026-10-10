using System.Text;

namespace Eunomia.Engine.Transfer.Tests;

/// <summary>
/// Deterministic fuzzing of the CSV reader (phase 14): the same file read with buffers of 16 (the minimum) to 80 characters gives
/// the same records as when read in one go (quotes, doubled quotes, multi-character terminators, line breaks
/// inside fields and <c>\N</c> may fall on a buffer boundary).
/// </summary>
public sealed class CsvFuzzTests
{
    private static readonly string[] Pieces =
    [
        "abc", "1", "", "\"q\"", "\"a;b\"", "\"x\"\"y\"", "\"multi\r\nline\"", "\\N", "NULL", "ação", "😀", " ", "\"",
    ];

    [Theory]
    [InlineData(";", "\r\n")]
    [InlineData(",", "\n")]
    [InlineData("\t", "\r\n")]
    [InlineData("||", "<EOL>")]
    public void BuffersPequenosDaoOsMesmosRegistos(string field, string line)
    {
        var options = new CsvOptions { FieldTerminator = field, LineTerminator = line, IgnoreLines = 0 };
        var random = new Random(field.Length * 31 + line.Length);
        for (var run = 0; run < 500; run++)
        {
            var sb = new StringBuilder();
            var rows = random.Next(0, 12);
            for (var r = 0; r < rows; r++)
            {
                var cols = random.Next(1, 6);
                for (var c = 0; c < cols; c++)
                {
                    if (c > 0)
                    {
                        sb.Append(field);
                    }
                    sb.Append(Pieces[random.Next(Pieces.Length)]);
                }
                if (r < rows - 1 || random.Next(2) == 0)
                {
                    sb.Append(line);
                }
            }
            var text = sb.ToString();
            var expected = ReadAll(text, options, 64 * 1024);
            var actual = ReadAll(text, options, random.Next(16, 81));
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }
    }

    private static List<string?[]> ReadAll(string text, CsvOptions options, int bufferSize)
    {
        var reader = new CsvReader(new StringReader(text), options, bufferSize);
        var records = new List<string?[]>();
        while (reader.Read() is { } record)
        {
            records.Add(record);
        }
        return records;
    }
}
