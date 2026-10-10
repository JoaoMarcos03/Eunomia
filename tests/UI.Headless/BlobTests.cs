using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Shell.Views;
using static Eunomia.UI.Headless.Tests.RealServer;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Save BLOB to file e Image preview (anexo D; popupDataGrid e Grid view options) contra o servidor.</summary>
public sealed class BlobTests
{
    /// <summary>PNG de ruído determinístico (bem mais de 256 bytes: no Data tab a célula vem cortada).</summary>
    private static byte[] NoisePng(int width, int height)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        var random = new Random(42);
        using (var frame = bitmap.Lock())
        {
            var pixels = new byte[frame.RowBytes * height];
            random.NextBytes(pixels);
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }
            Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task GravaOBlobInteiroEMostraAImagem()
    {
        var cs = ConnectionString();
        Assert.SkipWhen(cs is null, "Sem servidor de teste.");
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var png = NoisePng(40, 30);
        Assert.True(png.Length > 1000);
        var db = NewDatabaseName("eunomia_bl_");
        await ExecAsync(cs, $"CREATE DATABASE `{db}`; CREATE TABLE `{db}`.media (id INT PRIMARY KEY, photo LONGBLOB, notes TEXT); " +
            $"INSERT INTO `{db}`.media VALUES (1, 0x{Convert.ToHexString(png)}, 'ação_1 notes');");
        var folder = Path.Combine(Path.GetTempPath(), "eunomia-blob-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var (window, context, session, connector, _) = CreateShell(cs, db);
            var suggested = new List<string>();
            window.SaveBlobOverride = name =>
            {
                suggested.Add(name);
                return Path.Combine(folder, name);
            };
            Assert.True(await window.ConnectAsync(window, session, null));
            var model = window.Tree.Tree.Model!;
            var root = await WaitAsync(() => model.Roots.FirstOrDefault());
            var dbNode = await WaitAsync(() => root.Children?.FirstOrDefault(n => n.Name == db));
            await model.ExpandAsync(dbNode);
            window.ShowDataTab();
            window.Tree.Tree.FocusedNode = TreeModel.ObjectsOf(dbNode).Single(n => n.Name == "media");
            await WaitAsync(() => window.DataGrid.RowCount == 1 ? "ok" : null);

            // Save BLOB: a célula veio cortada (LEFT 256) e o ficheiro tem os bytes todos.
            window.DataGrid.Focus();
            window.DataGrid.Selection.Focus(0, 1);
            Assert.True(context.Commands.StateOf("grid.saveBlob").CanExecute);
            Assert.True(await context.Commands.ExecuteAsync("grid.saveBlob"));
            Assert.Equal("photo.png", suggested[^1]);
            Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(folder, "photo.png")));

            // Image preview: o painel aparece por baixo da árvore com a imagem da célula focada.
            Assert.True(await context.Commands.ExecuteAsync("grid.imagePreview"));
            Assert.True(context.Commands.StateOf("grid.imagePreview").IsChecked);
            await window.PreviewLoad;
            Assert.True(window.ImagePreview.IsVisible);
            Assert.Equal(new PixelSize(40, 30), window.ImagePreview.Bitmap?.PixelSize);
            Assert.StartsWith("40 x 30, PNG, ", window.ImagePreview.Info, StringComparison.Ordinal);

            // Texto: sem imagem; gravado em UTF-8 com .txt.
            window.DataGrid.Selection.Focus(0, 2);
            await window.PreviewLoad;
            Assert.Null(window.ImagePreview.Bitmap);
            Assert.Equal("No image", window.ImagePreview.Info);
            window.DataGrid.Focus();
            Assert.True(await context.Commands.ExecuteAsync("grid.saveBlob"));
            Assert.Equal("notes.txt", suggested[^1]);
            Assert.Equal("ação_1 notes"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(folder, "notes.txt")));

            // Close preview esconde o painel e desliga o toggle.
            window.ImagePreview.CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.False(window.ImagePreview.IsVisible);
            Assert.False(context.Commands.StateOf("grid.imagePreview").IsChecked);

            await connector.DisconnectAllAsync();
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            await ExecAsync(cs, $"DROP DATABASE IF EXISTS `{db}`");
        }
    }

    [AvaloniaTheory(Timeout = UiTimeouts.Default)]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task SnapshotDoPainel(string theme)
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var panel = new ImagePreviewPanel();
        var window = new Avalonia.Controls.Window
        {
            Content = panel,
            Width = 270,
            Height = 140,
            RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        window.Show();
        var bitmap = await Task.Run(() => new Bitmap(new MemoryStream(NoisePng(160, 90))));
        panel.Show(bitmap, "160 x 90, PNG, 42.0 KiB");
        window.CaptureRenderedFrame();

        Snapshot.Match(window.CaptureRenderedFrame(), $"image-preview-{theme}");
        window.Close();
    }
}
