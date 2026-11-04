using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>
/// JSON highlighting in the cell editor, with the SQL editor colours (theme tokens): property names as functions,
/// strings, numbers, and <c>true</c>/<c>false</c>/<c>null</c> as keywords. A JSON string never spans lines, so each line
/// is coloured on its own. Lines longer than <see cref="TextValueEditor.MaxHighlightedLineLength"/> stay plain (in
/// AvaloniaEdit, thousands of parts on one line cost quadratic time).
/// </summary>
public sealed class JsonColorizer : DocumentColorizingTransformer
{
    public SqlEditorPalette Palette { get; set; } = SqlEditorPalette.Light;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0 || line.Length > TextValueEditor.MaxHighlightedLineLength)
        {
            return;
        }
        var text = CurrentContext.Document.GetText(line.Offset, line.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                var end = StringEnd(text, i);
                var next = end;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }
                Paint(line, i, end, next < text.Length && text[next] == ':' ? Palette.Function : Palette.String);
                i = end;
            }
            else if (c == '-' || char.IsAsciiDigit(c))
            {
                var end = i + 1;
                while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] is '.' or 'e' or 'E' or '+' or '-'))
                {
                    end++;
                }
                Paint(line, i, end, Palette.Number);
                i = end;
            }
            else if (char.IsAsciiLetter(c))
            {
                var end = i + 1;
                while (end < text.Length && char.IsAsciiLetter(text[end]))
                {
                    end++;
                }
                var word = text.AsSpan(i, end - i);
                if (word is "true" or "false" or "null")
                {
                    Paint(line, i, end, Palette.Keyword);
                }
                i = end;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>End (exclusive) of the string starting at <paramref name="start"/>; without a closing quote, the end of the line.</summary>
    private static int StringEnd(string text, int start)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '"')
            {
                return i + 1;
            }
        }
        return text.Length;
    }

    private void Paint(DocumentLine line, int start, int end, IBrush brush) =>
        ChangeLinePart(line.Offset + start, line.Offset + Math.Min(end, line.Length), e => e.TextRunProperties.SetForegroundBrush(brush));
}
