using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Eunomia.UI.Controls.Tree;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Tree names use the full width when the row has no size to show (phase 13).</summary>
public sealed class TreeNameWidthTests
{
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task NameIsOnlyCutWhenItDoesNotFit()
    {
        var model = new TreeModel(SyntheticTreeProvider.CreateSessions(), new SyntheticTreeProvider());
        var view = new DatabaseTreeView();
        view.Tree.Model = model;
        view.Tree.ShowSizes = true;
        var session = model.Roots[0];
        await model.ExpandAsync(session);
        var db = session.Children![5];
        await model.ExpandAsync(db);
        var tables = TreeModel.ObjectsOf(db).Where(t => t.Name.Length >= 12).ToList();
        var noSize = tables[0];
        var withSize = tables[1];
        noSize.SizeBytes = null;
        withSize.SizeBytes = 123_456;
        var window = new Window { Content = view, Width = 230, Height = 600 };
        window.Show();
        window.CaptureRenderedFrame();

        // Without a size, the name uses the full width and fits; with a size, the Size column is reserved and the name is cut.
        Assert.False(view.Tree.IsNameClipped(noSize), noSize.Name);
        Assert.True(view.Tree.IsNameClipped(withSize), withSize.Name);
        window.Close();
    }
}
