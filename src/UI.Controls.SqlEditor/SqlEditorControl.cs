using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Eunomia.Engine.Sql;
using Eunomia.Engine.Sql.Completion;
using Eunomia.Engine.Sql.Lexing;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>
/// Editor SQL (secção 16): AvaloniaEdit com o colorizer do lexer MySQL e popup de completion com o
/// <see cref="CompletionProvider"/> (autocomplete A) ou a lista estática do <see cref="CompletionCatalog"/>. Consolas 9 pt (fallback Courier New), números de linha,
/// tab width 3 (secção 4.7). Completion com Ctrl+Space, ou 500 ms depois de <c>.</c>.
/// </summary>
public sealed partial class SqlEditorControl : Decorator, IEditTarget
{
    public static readonly TimeSpan CompletionDelayAfterDot = TimeSpan.FromMilliseconds(500);

    private readonly SqlColorizer _colorizer = new(SqlLanguages.Default);
    private readonly DispatcherTimer _dotTimer;
    private CompletionWindow? _completion;

    public SqlEditorControl()
    {
        Editor = new TextEditor
        {
            FontFamily = UiFonts.Monospace,
            FontSize = 12,
            ShowLineNumbers = false,
            WordWrap = false,
        };
        Editor.Options.IndentationSize = 3;
        Editor.Options.ConvertTabsToSpaces = false;
        Editor.Options.EnableHyperlinks = false;
        Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        Editor.TextArea.TextView.BackgroundRenderers.Add(new ErrorLineRenderer(this));
        ShowLineNumbers = true;
        Editor.Document.Changed += OnDocumentChanged;
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.TextArea.KeyDown += OnEditorKeyDown;
        Editor.ActualThemeVariantChanged += (_, _) => ApplyTheme();
        Editor.AttachedToVisualTree += (_, _) => ApplyTheme();
        Child = Editor;
        DragDrop.SetAllowDrop(Editor.TextArea, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        _dotTimer = new DispatcherTimer { Interval = CompletionDelayAfterDot };
        _dotTimer.Tick += (_, _) =>
        {
            _dotTimer.Stop();
            ShowCompletion();
        };
    }

    public TextEditor Editor { get; }

    private int? _errorLine;
    private readonly SessionLineNumberMargin _lineNumbers = new();
    private readonly Control _dottedLine = DottedLineMargin.Create();
    private uint _sessionColor;
    private SqlEditorPalette _palette = SqlEditorPalette.Light;

    /// <summary>
    /// Números de linha (gutter) e o traço a seguir. Os margens são do editor e não do <c>TextEditor.ShowLineNumbers</c>,
    /// para o gutter poder ter a cor da sessão.
    /// </summary>
    public bool ShowLineNumbers
    {
        get => Editor.TextArea.LeftMargins.Contains(_lineNumbers);
        set
        {
            var margins = Editor.TextArea.LeftMargins;
            if (value && !margins.Contains(_lineNumbers))
            {
                margins.Insert(0, _lineNumbers);
                margins.Insert(1, _dottedLine);
            }
            else if (!value)
            {
                margins.Remove(_lineNumbers);
                margins.Remove(_dottedLine);
            }
        }
    }

    /// <summary>
    /// Cor da sessão onde a query corre (0xAARRGGBB, 0 = nenhuma): tinta no fundo do gutter e uma barra com a cor
    /// inteira à esquerda (secção 5; FAM-S02).
    /// </summary>
    public uint SessionColor
    {
        get => _sessionColor;
        set
        {
            _sessionColor = value;
            ApplyGutter();
        }
    }

    private void ApplyGutter()
    {
        var background = ThemeTokens.Of(Editor.ActualThemeVariant).Argb("surface.content");
        _lineNumbers.Fill = _sessionColor == 0 ? null : new SolidColorBrush(Color.FromUInt32(ColorMath.SessionTint(_sessionColor, background)));
        _lineNumbers.Bar = _sessionColor == 0 ? null : new SolidColorBrush(Color.FromUInt32(_sessionColor | 0xFF000000));
        _lineNumbers.SetValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty, _palette.Gutter);
        if (_dottedLine is Avalonia.Controls.Shapes.Shape shape)
        {
            shape.Stroke = _palette.Gutter;
        }
        _lineNumbers.InvalidateVisual();
    }

    /// <summary>Números de linha com o fundo na tinta da cor da sessão e a barra da cor à esquerda.</summary>
    private sealed class SessionLineNumberMargin : LineNumberMargin
    {
        public IBrush? Fill { get; set; }

        public IBrush? Bar { get; set; }

        public override void Render(DrawingContext drawingContext)
        {
            if (Fill is { } fill)
            {
                drawingContext.FillRectangle(fill, new Avalonia.Rect(Bounds.Size));
            }
            if (Bar is { } bar)
            {
                drawingContext.FillRectangle(bar, new Avalonia.Rect(0, 0, 3, Bounds.Height));
            }
            base.Render(drawingContext);
        }
    }

    /// <summary>
    /// Linha (base 1) do último erro, a rosa (<c>#FFB7D2</c>) até à próxima edição (secção 4.7; FAM-Q11). Pôr a linha
    /// também leva o cursor ao início dela.
    /// </summary>
    public int? ErrorLine
    {
        get => _errorLine;
        set
        {
            if (_errorLine == value)
            {
                return;
            }
            _errorLine = value;
            if (value is { } line && line >= 1 && line <= Editor.Document.LineCount)
            {
                Editor.TextArea.Caret.Line = line;
                Editor.TextArea.Caret.Column = 1;
                Editor.ScrollToLine(line);
            }
            Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        }
    }

    private sealed class ErrorLineRenderer(SqlEditorControl owner) : AvaloniaEdit.Rendering.IBackgroundRenderer
    {
        private static readonly IBrush Light = new SolidColorBrush(ThemeTokens.Light.Color("log.error"));
        private static readonly IBrush Dark = new SolidColorBrush(ThemeTokens.Dark.Color("log.error"));

        public AvaloniaEdit.Rendering.KnownLayer Layer => AvaloniaEdit.Rendering.KnownLayer.Background;

        public void Draw(AvaloniaEdit.Rendering.TextView textView, DrawingContext drawingContext)
        {
            if (owner._errorLine is not { } number)
            {
                return;
            }
            foreach (var line in textView.VisualLines)
            {
                if (line.FirstDocumentLine.LineNumber == number)
                {
                    var y = line.VisualTop - textView.VerticalOffset;
                    var brush = owner.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? Dark : Light;
                    drawingContext.FillRectangle(brush, new Avalonia.Rect(0, y, textView.Bounds.Width + textView.HorizontalOffset, line.Height));
                }
            }
        }
    }

    /// <summary>
    /// Texto a inserir quando algo é largado no editor (nós da árvore, FAM-T07), ou <c>null</c> se não aceita. Recebe
    /// os modificadores do momento do drop (Shift qualifica o nome).
    /// </summary>
    public Func<IDataTransfer, KeyModifiers, string?>? DropText { get; set; }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (DropText?.Invoke(e.DataTransfer, e.KeyModifiers) is not null)
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    /// <summary>Insere o texto na posição do rato (ou no cursor, fora do texto).</summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DropText?.Invoke(e.DataTransfer, e.KeyModifiers) is not { } text)
        {
            return;
        }
        if (Editor.GetPositionFromPoint(e.GetPosition(Editor)) is { } position)
        {
            var offset = Editor.Document.GetOffset(position.Location);
            Editor.Select(offset, 0);
            Editor.CaretOffset = offset;
        }
        InsertAtCaret(text);
        e.Handled = true;
    }

    public CompletionCatalog Catalog { get; set; } = CompletionCatalog.CreateMySql([], []);

    /// <summary>
    /// Autocomplete com contexto (texto, cursor): quando existe, substitui o <see cref="Catalog"/>. É chamado na UI
    /// thread (o trabalho pesado vai para background); se o texto ou o cursor mudarem entretanto, a resposta é ignorada.
    /// </summary>
    public Func<string, int, CancellationToken, Task<CompletionProposal?>>? CompletionProvider { get; set; }

    private CancellationTokenSource? _proposal;

    /// <summary>Janela de completion aberta (para testes).</summary>
    public CompletionWindow? CompletionWindow => _completion;

    // IEditTarget: as actions do menu Edit (Copy, Cut, Paste, Select all, Undo) atuam no editor focado.
    public bool CanCopy => Editor.CanCopy;

    public bool CanCut => Editor.CanCut;

    public bool CanPaste => !Editor.IsReadOnly;

    public bool CanSelectAll => true;

    public bool CanUndo => Editor.CanUndo;

    public Task CopyAsync()
    {
        Editor.Copy();
        return Task.CompletedTask;
    }

    public Task CutAsync()
    {
        Editor.Cut();
        return Task.CompletedTask;
    }

    public Task PasteAsync()
    {
        Editor.Paste();
        return Task.CompletedTask;
    }

    public void SelectAll() => Editor.SelectAll();

    public void Undo() => Editor.Undo();

    public string Text
    {
        get => Editor.Text;
        set => Editor.Text = value;
    }

    /// <summary>
    /// Un/comment (secção 4.7): <c>-- </c> no início das linhas da seleção (ou da linha do cursor); se todas as linhas
    /// com texto já começam por <c>--</c>, tira-o. Uma só alteração no undo.
    /// </summary>
    public void ToggleComment()
    {
        var document = Editor.Document;
        var (first, last) = SelectedLines();
        var lines = Enumerable.Range(first, last - first + 1).Select(document.GetLineByNumber).ToList();
        var withText = lines.Where(l => document.GetText(l).Trim().Length > 0).ToList();
        if (withText.Count == 0)
        {
            return;
        }
        var commented = withText.All(l => document.GetText(l).TrimStart().StartsWith("--", StringComparison.Ordinal));
        using (document.RunUpdate())
        {
            foreach (var line in Enumerable.Reverse(withText))
            {
                var text = document.GetText(line);
                var indent = text.Length - text.TrimStart().Length;
                if (commented)
                {
                    var length = text.Length > indent + 2 && text[indent + 2] == ' ' ? 3 : 2;
                    document.Remove(line.Offset + indent, length);
                }
                else
                {
                    document.Insert(line.Offset, "-- ");
                }
            }
        }
    }

    /// <summary>Ctrl+U / Ctrl+L: maiúsculas ou minúsculas na seleção, ou na palavra do cursor.</summary>
    public void ChangeCase(bool upper)
    {
        var (start, length) = Editor.SelectionLength > 0 ? (Editor.SelectionStart, Editor.SelectionLength) : WordAtCaret();
        if (length == 0)
        {
            return;
        }
        var text = Editor.Document.GetText(start, length);
        var changed = upper ? text.ToUpperInvariant() : text.ToLowerInvariant();
        if (!string.Equals(text, changed, StringComparison.Ordinal))
        {
            var selected = Editor.SelectionLength > 0;
            Editor.Document.Replace(start, length, changed);
            if (selected)
            {
                Editor.Select(start, changed.Length);
            }
        }
    }

    /// <summary>Alt+↑ / Alt+↓: move as linhas da seleção (ou a do cursor) uma linha para cima ou para baixo.</summary>
    public void MoveLines(int direction)
    {
        var document = Editor.Document;
        var (first, last) = SelectedLines();
        if ((direction < 0 && first <= 1) || (direction > 0 && last >= document.LineCount))
        {
            return;
        }
        var firstLine = document.GetLineByNumber(first);
        var lastLine = document.GetLineByNumber(last);
        var block = document.GetText(firstLine.Offset, lastLine.EndOffset - firstLine.Offset);
        var selectionStart = Editor.SelectionStart;
        var selectionLength = Editor.SelectionLength;
        var caret = Editor.CaretOffset;
        int shift;
        if (direction < 0)
        {
            var previous = firstLine.PreviousLine!;
            var delimiter = document.GetText(previous.EndOffset, previous.DelimiterLength);
            var text = document.GetText(previous);
            document.Replace(previous.Offset, lastLine.EndOffset - previous.Offset, block + delimiter + text);
            shift = -(text.Length + delimiter.Length);
        }
        else
        {
            var next = lastLine.NextLine!;
            var delimiter = document.GetText(lastLine.EndOffset, lastLine.DelimiterLength);
            var text = document.GetText(next);
            document.Replace(firstLine.Offset, next.EndOffset - firstLine.Offset, text + delimiter + block);
            shift = text.Length + delimiter.Length;
        }
        if (selectionLength > 0)
        {
            Editor.Select(selectionStart + shift, selectionLength);
        }
        else
        {
            Editor.CaretOffset = caret + shift;
        }
    }

    /// <summary>Linhas da seleção (sem a última se a seleção acaba no início dela), ou a linha do cursor.</summary>
    private (int First, int Last) SelectedLines()
    {
        var document = Editor.Document;
        if (Editor.SelectionLength == 0)
        {
            var line = document.GetLineByOffset(Editor.CaretOffset).LineNumber;
            return (line, line);
        }
        var start = Editor.SelectionStart;
        var end = start + Editor.SelectionLength;
        var first = document.GetLineByOffset(start).LineNumber;
        var lastLine = document.GetLineByOffset(end);
        var last = lastLine.LineNumber > first && end == lastLine.Offset ? lastLine.LineNumber - 1 : lastLine.LineNumber;
        return (first, last);
    }

    private (int Start, int Length) WordAtCaret()
    {
        var text = Editor.Text;
        var start = Editor.CaretOffset;
        var end = start;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '$'))
        {
            start--;
        }
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$'))
        {
            end++;
        }
        return (start, end - start);
    }

    /// <summary>
    /// Insere texto na posição do cursor (substituindo a seleção, se houver), põe o cursor a seguir e
    /// devolve o foco ao editor. Usado pelo duplo clique na árvore (FAM-T07).
    /// </summary>
    public void InsertAtCaret(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = Editor.SelectionLength > 0 ? Editor.SelectionStart : Editor.CaretOffset;
        Editor.Document.Replace(start, Editor.SelectionLength, text);
        Editor.CaretOffset = start + text.Length;
        Editor.TextArea.Focus();
    }

    /// <summary>Abre o popup com as sugestões para a palavra antes do cursor.</summary>
    public void ShowCompletion()
    {
        _completion?.Close();
        if (CompletionProvider is { } provider)
        {
            _ = ShowProposalAsync(provider);
            return;
        }
        var (start, word) = WordBeforeCaret(Editor.TextArea);
        Open(start, word, Catalog.Find(word));
    }

    /// <summary>Tarefa do último pedido ao <see cref="CompletionProvider"/> (testes).</summary>
    public Task PendingCompletion { get; private set; } = Task.CompletedTask;

    private Task ShowProposalAsync(Func<string, int, CancellationToken, Task<CompletionProposal?>> provider)
    {
        _proposal?.Cancel();
        var cts = new CancellationTokenSource();
        _proposal = cts;
        var text = Editor.Text;
        var caret = Editor.CaretOffset;
        PendingCompletion = Run();
        return PendingCompletion;

        async Task Run()
        {
            CompletionProposal? proposal;
            try
            {
                proposal = await provider(text, caret, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(_proposal, cts))
                {
                    _proposal = null;
                }
                cts.Dispose();
            }
            if (proposal is null || Editor.CaretOffset != caret || !string.Equals(Editor.Text, text, StringComparison.Ordinal)
                || proposal.ReplaceStart < 0 || proposal.ReplaceStart > caret)
            {
                return;
            }
            var typed = text[proposal.ReplaceStart..caret].TrimStart('`');
            Open(proposal.ReplaceStart, typed, proposal.Items);
        }
    }

    private void Open(int start, string word, IReadOnlyList<CompletionItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }
        var area = Editor.TextArea;
        var window = new CompletionWindow(area)
        {
            StartOffset = start,
            Width = 350,
            MaxHeight = 12 * 18,
            CloseWhenCaretAtBeginning = true,
        };
        foreach (var item in items)
        {
            window.CompletionList.CompletionData.Add(new SqlCompletionData(item));
        }
        window.Closed += (_, _) => _completion = null;
        _completion = window;
        window.Show();
        if (word.Length > 0)
        {
            // SelectItem do AvaloniaEdit 12.0.0 dá NullReferenceException antes de o template da lista estar
            // aplicado (ListBox ainda null); filtra-se depois do layout do popup.
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_completion, window) && window.CompletionList.ListBox is not null)
                {
                    window.CompletionList.SelectItem(word);
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            ShowCompletion();
            e.Handled = true;
        }
    }

    /// <summary>Auto completion proposal depois de <c>.</c> (Preferences › SQL).</summary>
    public bool CompletionEnabled { get; set; } = true;

    /// <summary>Atraso do popup depois de <c>.</c> (500 ms por omissão).</summary>
    public TimeSpan CompletionDelay
    {
        get => _dotTimer.Interval;
        set => _dotTimer.Interval = value < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : value;
    }

    /// <summary>"Auto uppercase reserved words and functions" (ligado por omissão, como no HeidiSQL).</summary>
    public bool AutoUppercase { get; set; } = true;

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text == "." && CompletionEnabled)
        {
            _dotTimer.Stop();
            _dotTimer.Start();
        }
        else
        {
            _dotTimer.Stop();
        }
        if (AutoUppercase && e.Text is { Length: 1 } typed && !(char.IsLetterOrDigit(typed[0]) || typed[0] is '_' or '$'))
        {
            UppercaseWordBefore(Editor.CaretOffset - 1);
        }
    }

    /// <summary>Keyword, tipo ou função acabada de escrever (fora de strings e comentários) passa a maiúsculas.</summary>
    private void UppercaseWordBefore(int end)
    {
        var document = Editor.Document;
        if (_colorizer.TokenEndingAt(document, end) is not { Kind: SqlTokenKind.Keyword or SqlTokenKind.DataType or SqlTokenKind.Function } token)
        {
            return;
        }
        var word = document.GetText(token.Start, token.Length);
        var upper = word.ToUpperInvariant();
        if (!string.Equals(word, upper, StringComparison.Ordinal))
        {
            document.Replace(token.Start, token.Length, upper);
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        // A linha de erro fica marcada até à próxima edição (secção 4.7).
        ErrorLine = null;
        var line = Editor.Document.GetLineByOffset(Math.Min(e.Offset, Editor.Document.TextLength)).LineNumber;
        _colorizer.InvalidateFrom(line);
        // Uma string ou comentário aberto muda as linhas seguintes: redesenha o visível.
        Editor.TextArea.TextView.Redraw();
        ScheduleFoldings();
    }

    private void ApplyTheme()
    {
        var dark = Editor.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var palette = dark ? SqlEditorPalette.Dark : SqlEditorPalette.Light;
        _colorizer.Palette = palette;
        Editor.Background = palette.Background;
        Editor.Foreground = palette.Foreground;
        Editor.LineNumbersForeground = palette.Gutter;
        _palette = palette;
        ApplyGutter();
        ApplyFoldingTheme();
        Editor.TextArea.TextView.Redraw();
    }

    private static (int Start, string Word) WordBeforeCaret(TextArea area)
    {
        var document = area.Document;
        var offset = area.Caret.Offset;
        var start = offset;
        while (start > 0)
        {
            var c = document.GetCharAt(start - 1);
            if (!(char.IsLetterOrDigit(c) || c is '_' or '$'))
            {
                break;
            }
            start--;
        }
        return (start, document.GetText(start, offset - start));
    }

    /// <summary>Linha do popup: nome e, à direita e a cinzento, o tipo, o valor ou a assinatura (anexo C §4.2).</summary>
    private sealed class SqlCompletionData(CompletionItem item) : ICompletionData
    {
        public CompletionItem Item => item;

        public Avalonia.Media.IImage? Image => null;

        public string Text => item.Text;

        public object Content => item.Detail is null && !item.IsKey
            ? item.Text
            : new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = item.Text, FontWeight = item.IsKey ? FontWeight.Bold : FontWeight.Normal },
                    new TextBlock { Text = item.Detail ?? string.Empty, Margin = new Avalonia.Thickness(8, 0, 0, 0), Opacity = 0.6 },
                },
            };

        public object Description => item.Description ?? item.Detail ?? item.Kind.ToString();

        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, item.InsertText ?? item.Text);
    }

    /// <summary>Sugestões do popup aberto, pela ordem (testes).</summary>
    public IReadOnlyList<CompletionItem> CompletionItems =>
        _completion?.CompletionList.CompletionData.OfType<SqlCompletionData>().Select(d => d.Item).ToList() ?? [];
}
