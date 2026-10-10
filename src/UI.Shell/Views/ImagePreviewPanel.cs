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
/// Image preview (Grid view options ▸ Image preview; appendix C, <c>pnlPreview</c> below the tree): the image of the focused
/// cell, shrunk to fit (never enlarged), a line with the size and the format, and the Save BLOB to file and Close preview
/// buttons. The previous bitmap is released when another one arrives.
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

    /// <summary>Image shown (tests).</summary>
    public Bitmap? Bitmap => _image.Source as Bitmap;

    /// <summary>Line with the size and the format, or the reason there is no image.</summary>
    public string? Info => _info.Text;

    /// <summary>Shows an image (or none) with the information line; releases the previous one.</summary>
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
