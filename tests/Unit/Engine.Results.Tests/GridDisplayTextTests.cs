using System.Globalization;
using System.Text;

namespace Eunomia.Engine.Results.Tests;

/// <summary>
/// Large values in the grid (JSON from API responses): the grid keeps and draws only the start of the text, the whole
/// value stays in the block for copying, editing and exporting.
/// </summary>
public sealed class GridDisplayTextTests
{
    private static readonly ResultSchema Schema = new(
    [
        new ColumnInfo("doc", "LONGTEXT", TypeCategory.Text, StorageKind.String, false, true),
        new ColumnInfo("n", "INT", TypeCategory.Integer, StorageKind.Int64, false, true),
    ]);

    private static RowBlock Block(params string?[] values)
    {
        var b = new RowBlockBuilder(Schema, blockIndex: 0, firstRow: 0, rowCount: values.Length);
        for (var r = 0; r < values.Length; r++)
        {
            if (values[r] is { } v)
            {
                b.SetString(r, 0, v);
            }
            else
            {
                b.SetNull(r, 0);
            }
            b.SetInt64(r, 1, r);
        }
        return b.Build();
    }

    [Fact]
    public void LargeTextShowsItsStartInTheGridAndStaysWholeInRaw()
    {
        var json = "{\"items\":[" + string.Join(',', Enumerable.Range(0, 100_000).Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]}";
        var block = Block(json, "short", null);
        var cells = new DisplayFormatter(CultureInfo.InvariantCulture).FormatColumn(block, 0);

        Assert.Equal(json[..DisplayFormatter.MaxDisplayChars] + DisplayFormatter.TruncatedSuffix, cells[0]);
        Assert.Equal("short", cells[1]);
        Assert.Equal(DisplayFormatter.NullText, cells[2]);
        Assert.Equal(json, DisplayFormatter.FormatRaw(block, 0, 0));
        Assert.False(block.IsTruncated(0, 0));
    }

    [Fact]
    public void TextAtTheLimitStaysWhole()
    {
        var exact = new string('x', DisplayFormatter.MaxDisplayChars);
        var cells = new DisplayFormatter(CultureInfo.InvariantCulture).FormatColumn(Block(exact), 0);
        Assert.Equal(exact, cells[0]);
    }

    [Fact]
    public void CutDoesNotSplitMultiByteCharacters()
    {
        // "€" takes 3 bytes and the emoji 4 (two chars): the cut falls inside them and steps back to the character's start.
        var text = new string('a', DisplayFormatter.MaxDisplayChars - 1) + "€€" + new string('b', 10);
        var cell = new DisplayFormatter(CultureInfo.InvariantCulture).FormatColumn(Block(text), 0)[0];
        Assert.Equal(new string('a', DisplayFormatter.MaxDisplayChars - 1) + DisplayFormatter.TruncatedSuffix, cell);

        var emoji = new string('a', DisplayFormatter.MaxDisplayChars - 2) + "😀😀";
        var emojiCell = new DisplayFormatter(CultureInfo.InvariantCulture).FormatColumn(Block(emoji), 0)[0];
        Assert.Equal(new string('a', DisplayFormatter.MaxDisplayChars - 2) + DisplayFormatter.TruncatedSuffix, emojiCell);
        Assert.DoesNotContain((char)0xFFFD, emojiCell);
    }

    [Fact]
    public void LargeTextTakesItsExactBytesInTheBlock()
    {
        var text = new string('é', 1_000_000);
        var block = Block(text);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), block.GetUtf8(0, 0).Length);
        Assert.Equal(text, block.GetString(0, 0));
    }
}
