using Avalonia.Controls;
using Avalonia.Input.Platform;
using Eunomia.UI.Controls.Common.Text;

namespace Eunomia.UI.Controls.Common.TreeList;

/// <summary>
/// List header menu (<c>popupListHeader</c>, section 5.4): "Toggle visibility of all columns", "Copy column
/// names" and a checked entry per column. The first column always stays visible.
/// </summary>
public static class TreeListHeaderMenu
{
    public static void Show(TreeListControl list, Action changed)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(changed);
        var toggleAll = new MenuItem { Header = "Toggle visibility of all columns" };
        toggleAll.Click += (_, _) =>
        {
            var show = list.Columns.Skip(1).Any(c => !c.Visible);
            foreach (var column in list.Columns.Skip(1))
            {
                column.Visible = show;
            }
            list.InvalidateVisual();
            changed();
        };
        var copy = new MenuItem { Header = "Copy column names" };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(list)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(string.Join("\t", list.Columns.Where(c => c.Visible).Select(c => c.Header)));
            }
        };
        var items = new List<object> { toggleAll, copy, new Separator() };
        for (var i = 1; i < list.Columns.Count; i++)
        {
            var column = list.Columns[i];
            var item = new MenuItem { Header = LiteralText.Escaped(column.Header), ToggleType = MenuItemToggleType.CheckBox, IsChecked = column.Visible };
            item.Click += (_, _) =>
            {
                column.Visible = !column.Visible;
                list.InvalidateVisual();
                changed();
            };
            items.Add(item);
        }
        new ContextMenu { ItemsSource = items }.Open(list);
    }
}
