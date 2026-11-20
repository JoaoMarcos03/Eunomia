namespace Eunomia.Engine.Transfer.Tests;

/// <summary>A one-column CSV: a quoted empty field is an empty string, only a truly empty line is skipped.</summary>
public sealed class CsvQuotedEmptyTests
{
    [Fact]
    public void QuotedEmptyFieldIsARecordAndAnEmptyLineIsNot()
    {
        var reader = new CsvReader(new StringReader("header\r\na\r\n\"\"\r\n\r\nb\r\n"), new CsvOptions());
        var rows = new List<string?[]>();
        while (reader.Read() is { } row)
        {
            rows.Add(row);
        }

        Assert.Equal(["a", "", "b"], rows.Select(r => Assert.Single(r)));
    }
}
