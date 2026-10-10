using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Eunomia.App.Data;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Results;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Formatting;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Save BLOB to file (popupDataGrid) e Image preview (Grid view options; anexo D): o valor completo da célula focada (as
/// células cortadas no Data tab vêm do servidor pela chave), binários gravados tal como são e texto em UTF-8; o preview
/// mostra JPEG, GIF, BMP e PNG reconhecidos pelos primeiros bytes, por baixo da árvore.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Imagens maiores não são lidas para o preview (o valor vem inteiro para a memória).</summary>
    internal const int MaxPreviewBytes = 16 * 1024 * 1024;

    private readonly ImagePreviewPanel _preview = new() { IsVisible = false };
    private Grid _treeColumn = null!;
    private DataGridControl? _blobGrid;
    private CancellationTokenSource? _previewLoad;
    private bool _previewOn;

    /// <summary>Substitui o diálogo de gravar: nome sugerido → caminho, ou <c>null</c> para cancelar (testes).</summary>
    internal Func<string, string?>? SaveBlobOverride { get; set; }

    /// <summary>Painel do Image preview (testes).</summary>
    internal ImagePreviewPanel ImagePreview => _preview;

    /// <summary>A última carga do preview (testes).</summary>
    internal Task PreviewLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Árvore em cima, splitter e o painel do Image preview em baixo (escondidos até ligar o preview).</summary>
    private Control TreeColumn()
    {
        _treeColumn = new Grid { RowDefinitions = new RowDefinitions("*,0,0") };
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, IsVisible = false };
        Controls.Common.Theming.Themed.Bind(splitter, BackgroundProperty, "splitter");
        splitter.DragCompleted += (_, _) => RememberPanels();
        Grid.SetRow(splitter, 1);
        Grid.SetRow(_preview, 2);
        _treeColumn.Children.Add(_tree);
        _treeColumn.Children.Add(splitter);
        _treeColumn.Children.Add(_preview);
        _preview.SaveButton.Click += (_, _) => _ = SaveBlobAsync();
        _preview.CloseButton.Click += (_, _) => SetImagePreview(false);
        return _treeColumn;
    }

    private void InitBlob()
    {
        var c = _context.Commands;
        c.SetHandler("grid.saveBlob", _ => new ValueTask(SaveBlobAsync()), () => BlobGrid() is { Source: not null, Selection: { FocusedRow: >= 0, FocusedColumn: >= 0 } });
        Toggle("grid.imagePreview", () => _previewOn, SetImagePreview);
        WatchForPreview(_dataGrid.Control);
    }

    /// <summary>A grelha do Save BLOB: a focada, ou a do preview (o botão do painel tira o foco à grelha).</summary>
    private DataGridControl? BlobGrid() => FocusedGrid() ?? _blobGrid;

    /// <summary>A célula focada de uma grelha passa para o preview (Data tab e grelhas de resultados).</summary>
    private void WatchForPreview(DataGridControl grid)
    {
        grid.Selection.Changed += (_, _) =>
        {
            _blobGrid = grid;
            if (_previewOn)
            {
                PreviewLoad = LoadPreviewAsync(grid);
            }
        };
        grid.GotFocus += (_, _) => _blobGrid = grid;
    }

    private void SetImagePreview(bool on)
    {
        _previewOn = on;
        var rows = _treeColumn.RowDefinitions;
        if (on)
        {
            rows[1].Height = new GridLength(4);
            rows[2].Height = new GridLength(Math.Max(40, _context.Settings.Panels.PreviewHeight));
        }
        else
        {
            RememberPanels();
            rows[1].Height = new GridLength(0);
            rows[2].Height = new GridLength(0);
            _previewLoad?.Cancel();
            _preview.Show(null, string.Empty);
        }
        _treeColumn.Children[1].IsVisible = on;
        _preview.IsVisible = on;
        _ui.Refresh();
        if (on && _blobGrid is { } grid)
        {
            PreviewLoad = LoadPreviewAsync(grid);
        }
    }

    /// <summary>Lê a célula focada e mostra-a se for uma imagem; uma escolha seguinte cancela esta.</summary>
    private async Task LoadPreviewAsync(DataGridControl grid)
    {
        _previewLoad?.Cancel();
        var cts = new CancellationTokenSource();
        _previewLoad = cts;
        var (row, column) = (grid.Selection.FocusedRow, grid.Selection.FocusedColumn);
        if (grid.Source is not { } source || row < 0 || column < 0 || column >= source.Schema.Count)
        {
            _preview.Show(null, string.Empty);
            return;
        }
        try
        {
            // Mover as setas por várias células só lê a última.
            await Task.Delay(120, cts.Token);
            var raw = await grid.GetRawTextAsync(row, column);
            cts.Token.ThrowIfCancellationRequested();
            var binary = source.Schema[column].Storage == StorageKind.Binary;
            if (raw is null)
            {
                _preview.Show(null, "NULL");
                return;
            }
            if ((binary ? (raw.Length - 2) / 2 : raw.Length) > MaxPreviewBytes)
            {
                _preview.Show(null, "Too large to preview");
                return;
            }
            var bytes = BlobContent.ToBytes(raw, binary);
            if (BlobContent.ImageFormatOf(bytes) is not { } format)
            {
                _preview.Show(null, "No image");
                return;
            }
            var bitmap = await Task.Run(() => new Bitmap(new MemoryStream(bytes)), cts.Token);
            if (cts.IsCancellationRequested || !_previewOn)
            {
                bitmap.Dispose();
                return;
            }
            var size = SizeFormatter.Format(bytes.Length, CultureInfo.CurrentCulture);
            _preview.Show(bitmap, string.Create(CultureInfo.CurrentCulture, $"{bitmap.PixelSize.Width} x {bitmap.PixelSize.Height}, {format.ToString().ToUpperInvariant()}, {size}"));
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Fronteira do preview: uma imagem estragada ou um erro ao ler a célula não pode chegar ao diálogo de erro.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            if (!cts.IsCancellationRequested)
            {
                _preview.Show(null, ex is FormatException or ArgumentException or InvalidOperationException ? "Cannot read the image" : ex.Message);
            }
        }
    }

    /// <summary>Save BLOB to file: o valor completo da célula focada para um ficheiro (binário tal como é, texto em UTF-8).</summary>
    private async Task SaveBlobAsync()
    {
        if (BlobGrid() is not { Source: { } source } grid || grid.Selection.FocusedRow < 0 || grid.Selection.FocusedColumn < 0)
        {
            return;
        }
        var (row, column) = (grid.Selection.FocusedRow, grid.Selection.FocusedColumn);
        try
        {
            var raw = await grid.GetRawTextAsync(row, column);
            if (raw is null)
            {
                _context.Log.Append(LogCategory.Info, "The cell is NULL: nothing to save.");
                return;
            }
            var info = source.Schema[column];
            var binary = info.Storage == StorageKind.Binary;
            var bytes = BlobContent.ToBytes(raw, binary);
            var suggested = BlobContent.SuggestedFileName(info.Name, binary, bytes);
            var path = SaveBlobOverride is { } fake
                ? fake(suggested)
                : await PathPicker.SaveFileAsync(this, new FilePickerSaveOptions { Title = "Save BLOB to file", SuggestedFileName = suggested });
            if (path is null)
            {
                return;
            }
            await File.WriteAllBytesAsync(path, bytes);
            _context.Log.Append(LogCategory.Info, string.Create(CultureInfo.CurrentCulture, $"{bytes.Length:N0} bytes saved to {path}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or DbOperationException
                                       or Engine.Execution.SessionOperationException)
        {
            ShowError(ex);
        }
    }
}
