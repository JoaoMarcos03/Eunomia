using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Eunomia.App.Objects;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Features.DatabaseTab;
using Eunomia.UI.Features.HostTab;
using Eunomia.UI.Features.QueryTab;
using Eunomia.UI.Features.SchemaVisualizer;
using Eunomia.UI.Features.TableEditor;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Screen readers (roadmap 4.4; NVDA, Narrator, VoiceOver): every interactive control of every window has an accessible
/// name (what the screen reader reads).
/// </summary>
public sealed class AccessibilityTests
{
    private static readonly HashSet<AutomationControlType> Interactive =
    [
        AutomationControlType.Button,
        AutomationControlType.CheckBox,
        AutomationControlType.ComboBox,
        AutomationControlType.DataGrid,
        AutomationControlType.Edit,
        AutomationControlType.Hyperlink,
        AutomationControlType.List,
        AutomationControlType.RadioButton,
        AutomationControlType.Slider,
        AutomationControlType.Spinner,
        AutomationControlType.SplitButton,
        AutomationControlType.Tab,
        AutomationControlType.Tree,
    ];

    public static TheoryData<string> WindowTypes()
    {
        var data = new TheoryData<string>();
        foreach (var type in AllWindows.Factories.Keys.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            data.Add(type.Name);
        }
        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(WindowTypes))]
    public void ControlosInterativosTemNome(string windowType)
    {
        var window = AllWindows.Factories.Single(f => f.Key.Name == windowType).Value();
        window.Show();
        try
        {
            var unnamed = Unnamed(window);
            Assert.True(unnamed.Count == 0, $"{windowType}: sem nome acessível: {string.Join(", ", unnamed)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Tab views that only appear with a connected session (created as in the snapshot tests).</summary>
    private static readonly Dictionary<string, Func<Control>> TabViews = new(StringComparer.Ordinal)
    {
        ["Query"] = () => new QueryTabView(150, 220) { Helpers = new QueryHelpersView() },
        ["TableEditor"] = () =>
        {
            var design = new TableDesign { Database = "shop", Name = "people" };
            design.Columns.Add(new ColumnDesign { Name = "id", DataType = "INT" });
            design.Indexes.Add(new IndexDesign { Name = "PRIMARY", Kind = IndexKind.Primary, Columns = "id" });
            design.ForeignKeys.Add(new ForeignKeyDesign { Name = "fk_owner", Columns = "id" });
            design.Checks.Add(new CheckDesign { Name = "positive", Clause = "id > 0" });
            return new TableEditorView(design, new TableEditorLists([], ["InnoDB"], []), _ => "CREATE", null);
        },
        ["ObjectCode"] = () => new ObjectCodeEditorView(DbObjectKind.View, "CREATE VIEW v AS SELECT 1", isNew: false),
        ["Host"] = () => new HostTabView(),
        ["Database"] = () => new DatabaseTabView(),
        ["SchemaVisualizer"] = () => new SchemaVisualizerView("shop"),
    };

    public static TheoryData<string> TabViewNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in TabViews.Keys.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }
        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(TabViewNames))]
    public void VistasDasTabsTemNome(string view)
    {
        var window = new Window { Content = TabViews[view](), Width = 1000, Height = 600 };
        window.Show();
        try
        {
            var unnamed = Unnamed(window);
            Assert.True(unnamed.Count == 0, $"{view}: sem nome acessível: {string.Join(", ", unnamed)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Interactive controls without a name, as <c>Type(Name) [where]</c>. Walks the logical tree (includes hidden panels and
    /// unselected tabs); template parts belong to the control that has them.
    /// </summary>
    internal static List<string> Unnamed(Control root) =>
        [.. root.GetLogicalDescendants().OfType<Control>().Prepend(root)
            .Where(c => c.TemplatedParent is null)
            .Select(c => (Control: c, Peer: ControlAutomationPeer.CreatePeerForElement(c)))
            .Where(p => Interactive.Contains(p.Peer.GetAutomationControlType()) && string.IsNullOrWhiteSpace(p.Peer.GetName()))
            .Select(p => $"{p.Control.GetType().Name}({p.Control.Name}) [{Where(p.Control)}]")];

    /// <summary>To find the control in the code: the label next to it, the placeholder and the containers.</summary>
    private static string Where(Control control)
    {
        var siblings = (control.GetLogicalParent() as Panel)?.Children;
        var index = siblings?.IndexOf(control) ?? -1;
        var label = siblings is not null && index > 0 && siblings[index - 1] is TextBlock text ? text.Text : null;
        var placeholder = (control as TextBox)?.PlaceholderText;
        var parents = string.Join(" < ", control.GetLogicalAncestors().Take(4).Select(a => a.GetType().Name));
        return $"label: {label}; placeholder: {placeholder}; in {parents}";
    }
}
