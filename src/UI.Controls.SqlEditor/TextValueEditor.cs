using Avalonia.Controls;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Eunomia.Engine.Sql;
using Eunomia.Engine.Sql.Formatting;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>
/// Editor for a cell value (appendix C §3.18, <c>texteditor.pas</c>): AvaloniaEdit with line numbers, word wrap and
/// highlighting by <see cref="TextSyntax"/> in the theme colours. Unlike a <c>TextBox</c>, it only draws the visible
/// lines, so large values (JSON from API responses, logs) open without freezing the application. The text stays exactly
/// as it came (line breaks, tabs, control characters): nothing is converted when opening or reading it.
/// </summary>
public sealed class TextValueEditor : Decorator
{
    /// <summary>Lines longer than this are not highlighted (HeidiSQL turns highlighting off above 10 KB).</summary>
    public const int MaxHighlightedLineLength = 10_000;

    private readonly JsonColorizer _json = new();
    private readonly MarkupColorizer _markup = new();
    private readonly SqlColorizer _sql = new(SqlLanguages.Default);
    private IVisualLineTransformer? _active;
    private TextSyntax _syntax;

    public TextValueEditor()
    {
        Editor = new TextEditor
        {
            FontFamily = UiFonts.Monospace,
            FontSize = 12,
            ShowLineNumbers = true,
            WordWrap = true,
        };
        Editor.Options.ConvertTabsToSpaces = false;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.EnableRectangularSelection = false;
        Editor.Document.Changed += (_, e) =>
        {
            var line = Editor.Document.GetLineByOffset(Math.Min(e.Offset, Editor.Document.TextLength)).LineNumber;
            _markup.InvalidateFrom(line);
            _sql.InvalidateFrom(line);
        };
        Editor.ActualThemeVariantChanged += (_, _) => ApplyTheme();
        Editor.AttachedToVisualTree += (_, _) => ApplyTheme();
        Child = Editor;
    }

    public TextEditor Editor { get; }

    public string Text
    {
        get => Editor.Document.Text;
        set => Editor.Document.Text = value ?? string.Empty;
    }

    public bool WordWrap
    {
        get => Editor.WordWrap;
        set => Editor.WordWrap = value;
    }

    /// <summary>Current highlighting; changing it does not touch the text.</summary>
    public TextSyntax Syntax
    {
        get => _syntax;
        set
        {
            _syntax = value;
            var transformers = Editor.TextArea.TextView.LineTransformers;
            if (_active is not null)
            {
                transformers.Remove(_active);
            }
            _active = value switch
            {
                TextSyntax.Json => _json,
                TextSyntax.Xml or TextSyntax.Html => _markup,
                TextSyntax.Sql => _sql,
                _ => null,
            };
            _markup.InvalidateFrom(1);
            _sql.InvalidateFrom(1);
            if (_active is not null)
            {
                transformers.Add(_active);
            }
            Editor.TextArea.TextView.Redraw();
        }
    }

    /// <summary>Some line is too long to be highlighted (the status line says so).</summary>
    public bool HasUnhighlightedLines
    {
        get
        {
            foreach (var line in Editor.Document.Lines)
            {
                if (line.Length > MaxHighlightedLineLength)
                {
                    return true;
                }
            }
            return false;
        }
    }

    private void ApplyTheme()
    {
        var palette = Editor.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? SqlEditorPalette.Dark : SqlEditorPalette.Light;
        _json.Palette = palette;
        _markup.Palette = palette;
        _sql.Palette = palette;
        Editor.Background = palette.Background;
        Editor.Foreground = palette.Foreground;
        Editor.LineNumbersForeground = palette.Gutter;
        Editor.TextArea.TextView.Redraw();
    }
}
