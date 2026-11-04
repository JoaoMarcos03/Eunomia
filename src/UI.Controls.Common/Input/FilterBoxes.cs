using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Icons;

namespace Eunomia.UI.Controls.Common.Input;

/// <summary>
/// Filter boxes like HeidiSQL's (TButtonedEdit; appendix C §1.9 and §3: Database/Table filter, filter panel, helpers
/// filter): the filter icon on the left and a clear button on the right, shown only while there is text (not only while
/// focused, as Fluent's <c>clearButton</c> class does). Clearing empties the text, so the owner's <c>TextChanged</c>
/// applies the filter as if the user had deleted everything; focus goes back to the box.
/// </summary>
public static class FilterBoxes
{
    /// <summary>Accessible name and tooltip of the clear button.</summary>
    public const string ClearCaption = "Clear filter";

    private const double IconSize = 14;

    /// <summary>
    /// Adds the <paramref name="icon"/> on the left of <paramref name="box"/> and the clear button on the right;
    /// <paramref name="cleared"/> runs after clearing (for instance, to apply a debounced filter at once).
    /// </summary>
    public static TextBox WithClearButton(this TextBox box, string icon = "filter", Action? cleared = null)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(icon);
        var left = new Image { Width = IconSize, Height = IconSize, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var clearImage = new Image { Width = IconSize, Height = IconSize };
        var clear = new Button
        {
            Content = clearImage,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 3, 0),
            MinHeight = 0,
            MinWidth = 0,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Arrow),
            IsVisible = !string.IsNullOrEmpty(box.Text),
        };
        AutomationProperties.SetName(clear, ClearCaption);
        ToolTip.SetTip(clear, ClearCaption);
        clear.Click += (_, e) =>
        {
            e.Handled = true;
            box.Text = string.Empty;
            box.Focus();
            cleared?.Invoke();
        };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                clear.IsVisible = !string.IsNullOrEmpty(box.Text);
            }
        };
        box.InnerLeftContent = left;
        box.InnerRightContent = clear;

        // The icons follow the theme (dark inks are lightened in the dark theme). Subscribed only while the box is in a
        // window, so the static event does not keep boxes of closed dialogs alive.
        void Refresh(object? sender, EventArgs e)
        {
            left.Source = IconFactory.Get(icon);
            clearImage.Source = IconFactory.Get("filter-clear");
        }
        Refresh(null, EventArgs.Empty);
        box.AttachedToVisualTree += (_, _) =>
        {
            IconFactory.ThemeChanged += Refresh;
            Refresh(null, EventArgs.Empty);
        };
        box.DetachedFromVisualTree += (_, _) => IconFactory.ThemeChanged -= Refresh;
        return box;
    }

    /// <summary>Clear button of a box prepared by <see cref="WithClearButton"/> (tests and automation).</summary>
    public static Button? ClearButtonOf(TextBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        return box.InnerRightContent as Button;
    }
}
