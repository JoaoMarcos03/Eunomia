using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Icons;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Shell.Views;

/// <summary>
/// Image preview (Grid view options ▸ Image preview; anexo C, <c>pnlPreview</c> por baixo da árvore): a imagem da célula
/// focada, reduzida para caber (nunca ampliada), uma linha com o tamanho e o formato, e os botões Save BLOB to file e
/// Close preview. O bitmap anterior é libertado quando chega outro.
/// </summary>
public sealed class ImagePreviewPanel : DockPanel
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Name = "previewImage" };
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0) };

    public ImagePreviewPanel()
    {
        SaveButton = ToolButton("save", "Save BLOB to file ...", "btnPreviewSave");
        CloseButton = ToolButton("close-tab", "Close preview", "btnPreviewClose");
        var bar = new DockPanel { Margin = new Thickness(2) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        buttons.Children.Add(SaveButton);
        buttons.Children.Add(CloseButton);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(_info);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(new Border { Child = _image, Padding = new Thickness(4) });
        Themed.Bind(this, BackgroundProperty, "surface.content");
        this.Named("Image preview");
        _image.Named("Image preview");
    }

    public Button SaveButton { get; }

    public Button CloseButton { get; }

    /// <summary>Imagem mostrada (testes).</summary>
    public Bitmap? Bitmap => _image.Source as Bitmap;

    /// <summary>Linha com o tamanho e o formato, ou o motivo de não haver imagem.</summary>
    public string? Info => _info.Text;

    /// <summary>Mostra uma imagem (ou nenhuma) com a linha de informação; liberta a anterior.</summary>
    public void Show(Bitmap? bitmap, string info)
    {
        var previous = _image.Source as Bitmap;
        _image.Source = bitmap;
        _info.Text = info;
        ToolTip.SetTip(_info, info);
        if (!ReferenceEquals(previous, bitmap))
        {
            previous?.Dispose();
        }
    }

    private static Button ToolButton(string icon, string caption, string name)
    {
        var button = new Button
        {
            Content = IconFactory.CreateImage(icon),
            Padding = new Thickness(3),
            MinWidth = 22,
            MinHeight = 22,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Name = name,
        };
        ToolTip.SetTip(button, caption);
        return button.Named(caption);
    }
}
