using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Eunomia.Engine.Sql;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>Cor de fundo de uma linha do SQL log (anexo C §1.10).</summary>
public enum LogLineStyle
{
    Normal,
    Error,
    Warning,
    Note,
    Info,
}

/// <summary>
/// SQL log (secção 5.3, anexo C §1.10): só de leitura, uma linha por entrada, gutter com números contínuos (a
/// numeração não recomeça quando as linhas antigas saem), highlight SQL, fundo por tipo (erro #FFB7D2, Warning
/// #FFCDB7, Note #FFF7D3, Info #ECFFC6) e scroll para o fim a cada linha nova. Scrollbar horizontal opcional.
/// </summary>
public sealed class SqlLogView : Decorator, IEditTarget
{
    private readonly List<LogLineStyle> _styles = [];
    private readonly LogNumberMargin _numbers;
    private readonly SqlColorizer _colorizer = new(SqlLanguages.Default);
    private long _firstNumber = 1;

    public SqlLogView()
    {
        Editor = new TextEditor
        {
            FontFamily = UiFonts.Monospace,
            FontSize = 12,
            IsReadOnly = true,
            ShowLineNumbers = false,
            WordWrap = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
        };
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        _numbers = new LogNumberMargin(this);
        Editor.TextArea.LeftMargins.Insert(0, _numbers);
        Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        Editor.TextArea.TextView.BackgroundRenderers.Add(new LineBackgroundRenderer(this));
        Editor.ActualThemeVariantChanged += (_, _) => ApplyTheme();
        Editor.AttachedToVisualTree += (_, _) => ApplyTheme();
        Child = Editor;
    }

    public TextEditor Editor { get; }

    public long FirstLineNumber => _firstNumber;

    // IEditTarget: o log é só de leitura (Copy e Select all).
    bool IEditTarget.CanCopy => Editor.CanCopy;

    bool IEditTarget.CanCut => false;

    bool IEditTarget.CanPaste => false;

    bool IEditTarget.CanSelectAll => true;

    bool IEditTarget.CanUndo => false;

    Task IEditTarget.CopyAsync()
    {
        Editor.Copy();
        return Task.CompletedTask;
    }

    Task IEditTarget.CutAsync() => Task.CompletedTask;

    Task IEditTarget.PasteAsync() => Task.CompletedTask;

    void IEditTarget.SelectAll() => Editor.SelectAll();

    void IEditTarget.Undo()
    {
    }

    public int LineCount => _styles.Count;

    public string Text => Editor.Document.Text;

    public bool HorizontalScrollbar
    {
        get => Editor.HorizontalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        set => Editor.HorizontalScrollBarVisibility = value
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
    }

    /// <summary>Substitui o conteúdo inteiro (arranque, Clear, preferências mudadas).</summary>
    public void Reset(long firstLineNumber, IReadOnlyList<(string Text, LogLineStyle Style)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _firstNumber = firstLineNumber;
        _styles.Clear();
        _styles.AddRange(lines.Select(l => l.Style));
        Editor.Document.Text = string.Join("\n", lines.Select(l => l.Text));
        Editor.Document.UndoStack.ClearAll();
        _numbers.InvalidateMeasure();
        Editor.ScrollToEnd();
    }

    /// <summary>Acrescenta linhas no fim e corta <paramref name="trimFromStart"/> linhas do início.</summary>
    public void Append(IReadOnlyList<(string Text, LogLineStyle Style)> lines, int trimFromStart)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var document = Editor.Document;
        document.BeginUpdate();
        try
        {
            foreach (var (text, style) in lines)
            {
                document.Insert(document.TextLength, (_styles.Count > 0 ? "\n" : string.Empty) + text);
                _styles.Add(style);
            }
            trimFromStart = Math.Min(trimFromStart, _styles.Count);
            if (trimFromStart > 0)
            {
                var end = trimFromStart < document.LineCount
                    ? document.GetLineByNumber(trimFromStart + 1).Offset
                    : document.TextLength;
                document.Remove(0, end);
                _styles.RemoveRange(0, trimFromStart);
                _firstNumber += trimFromStart;
            }
        }
        finally
        {
            document.EndUpdate();
        }
        document.UndoStack.ClearAll();
        _numbers.InvalidateMeasure();
        Editor.ScrollToEnd();
    }

    /// <summary>Texto da linha do documento (1-based), para "Copy line to new query tab".</summary>
    public string LineText(int documentLine)
    {
        var line = Editor.Document.GetLineByNumber(documentLine);
        return Editor.Document.GetText(line.Offset, line.Length);
    }


    private void ApplyTheme()
    {
        var palette = Editor.ActualThemeVariant == ThemeVariant.Dark ? SqlEditorPalette.Dark : SqlEditorPalette.Light;
        _colorizer.Palette = palette;
        Editor.Background = palette.Background;
        Editor.Foreground = palette.Foreground;
        Editor.TextArea.TextView.Redraw();
        _numbers.InvalidateVisual();
    }
    private LogLineStyle StyleOf(int documentLine) =>
        documentLine >= 1 && documentLine <= _styles.Count ? _styles[documentLine - 1] : LogLineStyle.Normal;

    private static Color BackgroundOf(LogLineStyle style, bool dark)
    {
        var theme = dark ? ThemeTokens.Dark : ThemeTokens.Light;
        return style switch
        {
            LogLineStyle.Error => theme.Color("log.error"),
            LogLineStyle.Warning => theme.Color("log.warning"),
            LogLineStyle.Note => theme.Color("log.note"),
            LogLineStyle.Info => theme.Color("log.info"),
            _ => Colors.Transparent,
        };
    }

    /// <summary>Fundo das linhas por tipo, por baixo do texto e da seleção.</summary>
    private sealed class LineBackgroundRenderer(SqlLogView owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var dark = owner.ActualThemeVariant == ThemeVariant.Dark;
            foreach (var line in textView.VisualLines)
            {
                var style = owner.StyleOf(line.FirstDocumentLine.LineNumber);
                if (style == LogLineStyle.Normal)
                {
                    continue;
                }
                var y = line.VisualTop - textView.VerticalOffset;
                var rect = new Rect(0, y, textView.Bounds.Width + textView.HorizontalOffset, line.Height);
                drawingContext.FillRectangle(new SolidColorBrush(BackgroundOf(style, dark)), rect);
            }
        }
    }

    /// <summary>Gutter com a numeração contínua do log (primeira linha = <see cref="FirstLineNumber"/>).</summary>
    private sealed class LogNumberMargin(SqlLogView owner) : AbstractMargin
    {
        private const double Padding = 6;

        protected override Size MeasureOverride(Size availableSize)
        {
            var last = owner._firstNumber + Math.Max(0, owner._styles.Count - 1);
            var digits = Math.Max(3, last.ToString(CultureInfo.InvariantCulture).Length);
            return new Size(Layout(new string('9', digits)).Width + (2 * Padding), 0);
        }

        public override void Render(DrawingContext context)
        {
            var view = TextView;
            var dark = owner.ActualThemeVariant == ThemeVariant.Dark;
            var theme = dark ? ThemeTokens.Dark : ThemeTokens.Light;
            context.FillRectangle(new SolidColorBrush(theme.Color("log.gutter")), new Rect(Bounds.Size));
            if (view is null || !view.VisualLinesValid)
            {
                return;
            }
            var brush = new SolidColorBrush(theme.Color("log.gutter.text"));
            foreach (var line in view.VisualLines)
            {
                var number = owner._firstNumber + line.FirstDocumentLine.LineNumber - 1;
                var text = Layout(number.ToString(CultureInfo.InvariantCulture), brush);
                var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - view.VerticalOffset;
                context.DrawText(text, new Point(Bounds.Width - Padding - text.Width, y));
            }
        }

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null)
            {
                oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            }
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null)
            {
                newTextView.VisualLinesChanged += OnVisualLinesChanged;
            }
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        private FormattedText Layout(string text, IBrush? brush = null) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(owner.Editor.FontFamily), owner.Editor.FontSize, brush ?? Brushes.Gray);
    }
}
