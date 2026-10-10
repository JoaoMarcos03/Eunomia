using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Eunomia.App.Commands;
using Eunomia.App.Settings;
using Eunomia.Engine.Results.Synthetic;
using Eunomia.Engine.Sql;
using Eunomia.Engine.Sql.Completion;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Input;
using Eunomia.UI.Controls.Common.Theming;
using Eunomia.UI.Controls.Grid;
using Eunomia.UI.Controls.SqlEditor;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Shell.Commands;
using Eunomia.UI.Shell.Input;
using Eunomia.UI.Shell.Sessions;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Main window (section 5.1, FAM-L01): menu and toolbar generated from the command registry, tree with filters on the
/// left, tabs on the right with the filter panel below, SQL log and a 7-panel status bar. Panel dimensions are
/// persisted (FAM-L07). Shortcuts go through the keymap first (like HeidiSQL's actions, which are seen before
/// the focused control) and resolve by focus context.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly ShellContext _context;
    private readonly CommandUi _ui;
    private readonly Grid _body;
    private readonly Grid _root;
    private readonly DatabaseTreeView _tree;
    private readonly TreeModel _model;
    private readonly DataGridView _dataGrid;
    private readonly UI.Features.DataTab.DataTabView _dataView;
    private readonly TextBlock _dataLabel;
    private readonly MainTabsView _tabs;
    private readonly Border _filterPanel;
    private readonly TextBox _filterBox;
    private readonly TextBlock _filterInfo;
    private readonly SqlLogView _log;
    private readonly GridSplitter _logSplitter;
    private readonly Control _toolbar;

    public MainWindow(ShellContext context)
    {
        Icon = BrandAssets.WindowIcon;
        _context = context;
        var settings = context.Settings;
        Title = "Eunomia " + ShellContext.Version;
        Width = settings.MainWindow.Width;
        Height = settings.MainWindow.Height;
        Position = new PixelPoint(settings.MainWindow.Left, settings.MainWindow.Top);
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (settings.MainWindow.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
        FontFamily = UiFonts.Ui;
        FontSize = 12;
        Themed.Bind(this, BackgroundProperty, "surface.panel");
        // Icons in the main window's theme (tests change the theme per window).
        ActualThemeVariantChanged += (_, _) => Controls.Common.Icons.IconFactory.SetDark(ActualThemeVariant == ThemeVariant.Dark);
        AddShellStyles();
        ShellTrace.At("shell-styles");

        _ui = new CommandUi(context.Commands)
        {
            DropdownEntries = DropdownEntries,
            DynamicItems = kind => kind switch
            {
                DynamicMenus.ConnectTo or DynamicMenus.Sessions => SessionMenuItems(),
                MainMenuLayout.QuickFilterValues => MoreValuesItems(),
                DynamicMenus.EditorCommands => EditorCommandItems(),
                DynamicMenus.LoadSql => LoadSqlItems(),
                _ => [],
            },
        };
        _ui.CommandFailed += (_, ex) => ShowError(ex);

        // Tree (synthetic data until phase 3/4).
        // Real sessions when the composition root provides them; without them (measurements, tests), the spike's synthetic tree.
        _sessionTree = context.Sessions is { } sessions ? new SessionTreeProvider(sessions.Connector, CurrentTreeLayout) : null;
        _model = _sessionTree is not null
            ? new TreeModel([], _sessionTree)
            : new TreeModel(SyntheticTreeProvider.CreateSessions(), new SyntheticTreeProvider(latency: TimeSpan.FromMilliseconds(15)));
        _tree = new DatabaseTreeView();
        _tree.Tree.Model = _model;
        CommandScope.SetContext(_tree.Tree, CommandContexts.Tree);
        _model.FilterError += (_, message) => context.Log.Append(LogCategory.Info, "Filter error: " + message);
        _tree.Tree.FocusedNodeChanged += (_, _) => OnTreeFocusChanged();
        _tree.Tree.NodeDoubleClicked += OnTreeDoubleClicked;
        ShellTrace.At("shell-tree");

        // Data tab (section 4.4): label, Next, Show all, Sorting, Columns, Filter and the grid.
        _dataView = new UI.Features.DataTab.DataTabView();
        _dataView.SetPaging(false);
        _dataLabel = _dataView.Label;
        _dataGrid = _dataView.Grid;
        CommandScope.SetContext(_dataGrid, CommandContexts.Grid);
        _dataGrid.Control.StatusChanged += (_, _) => context.Status.Position = _dataGrid.Control.Selection.StatusText;
        RenderStartMarker = Marker();
        RenderEndMarker = Marker();
        var dataTab = new Panel();
        dataTab.Children.Add(RenderStartMarker);
        dataTab.Children.Add(_dataView);
        dataTab.Children.Add(RenderEndMarker);

        _tabs = new MainTabsView(settings.Tabs, CreateQueryContent);
        _tabs.DataTab.Content = dataTab;
        _tabs.HostTab.Content = Placeholder("Host: databases and processes (phase 4)");
        _tabs.DatabaseTab.Content = Placeholder("Database: object list (phase 4)");
        _tabs.SelectionChanged += (_, _) => OnActiveTabChanged();
        ShellTrace.At("shell-tabs");

        // Filter panel (Ctrl+Alt+F): local regex over the active list (FAM-G14, wired in phase 5).
        _filterBox = new TextBox { PlaceholderText = "Regular expression", Width = 260, VerticalAlignment = VerticalAlignment.Center, MinHeight = 22, Padding = new Thickness(4, 1) }.Named("Filter");
        var filterClose = new Button { Content = Controls.Common.Icons.IconFactory.CreateImage("close-tab"), Padding = new Thickness(1), Focusable = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        filterClose.Click += (_, _) => SetFilterPanel(false);
        var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 2) };
        filterRow.Children.Add(filterClose);
        filterRow.Children.Add(new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center });
        filterRow.Children.Add(_filterBox);
        _filterInfo = new TextBlock { Name = "filterInfo", VerticalAlignment = VerticalAlignment.Center };
        filterRow.Children.Add(_filterInfo);
        _filterPanel = new Border { Child = filterRow, Height = 26, IsVisible = settings.Display.FilterPanel };
        var right = new DockPanel();
        DockPanel.SetDock(_filterPanel, Dock.Bottom);
        right.Children.Add(_filterPanel);
        right.Children.Add(_tabs);

        _body = new Grid { ColumnDefinitions = new ColumnDefinitions($"{settings.Panels.TreeWidth.ToString(CultureInfo.InvariantCulture)},4,*") };
        _body.ColumnDefinitions[0].MinWidth = 40;
        var bodySplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Themed.Bind(bodySplitter, BackgroundProperty, "splitter");
        bodySplitter.DragCompleted += (_, _) => RememberPanels();
        Grid.SetColumn(bodySplitter, 1);
        Grid.SetColumn(right, 2);
        _body.Children.Add(TreeColumn());
        _body.Children.Add(bodySplitter);
        _body.Children.Add(right);
        _tree.FiltersVisible = settings.Display.TreeFilters;

        _log = new SqlLogView { MinHeight = 0, HorizontalScrollbar = settings.Log.HorizontalScrollbar };
        CommandScope.SetContext(_log, CommandContexts.Log);
        _log.ContextMenu = _ui.BuildContextMenu(SqlLogMenu);
        context.Log.Changed += OnLogChanged;
        ResetLogView();
        ShellTrace.At("shell-log");

        var status = new StatusBarView(context.Status);
        status.ServerPanelClicked += (_, _) => _ui.Execute("session.properties");

        _toolbar = _ui.BuildToolbar(MainMenuLayout.Toolbar);
        ShellTrace.At("shell-toolbar");
        var menu = _ui.BuildMainMenu(MainMenuLayout.Menus);
        ShellTrace.At("shell-menu");

        _root = new Grid { RowDefinitions = new RowDefinitions($"Auto,Auto,*,4,{settings.Panels.LogHeight.ToString(CultureInfo.InvariantCulture)},Auto") };
        _logSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        Themed.Bind(_logSplitter, BackgroundProperty, "splitter");
        _logSplitter.DragCompleted += (_, _) => RememberPanels();
        Grid.SetRow(_toolbar, 1);
        Grid.SetRow(_body, 2);
        Grid.SetRow(_logSplitter, 3);
        Grid.SetRow(_log, 4);
        Grid.SetRow(status, 5);
        _root.Children.Add(menu);
        _root.Children.Add(_toolbar);
        _root.Children.Add(_body);
        _root.Children.Add(_logSplitter);
        _root.Children.Add(_log);
        _root.Children.Add(status);
        Content = _root;
        SetLogPanel(settings.Display.LogPanel);

        RegisterCommands();
        InitTree();
        InitObjectCommands();
        InitLists();
        InitAutoRefresh();
        InitDataTab();
        InitQuickFilters();
        InitGridLinks();
        InitSqlHelp();
        InitFindText();
        InitMaintenance();
        InitBlob();
        InitCopyTable();
        InitCommandPalette();
        InitLocalFilter();
        InitExport();
        InitSqlExport();
        InitRunFile();
        InitCsvImport();
        InitDiagram();
        InitQueryCommands();
        InitSearch();
        InitFiles();
        InitHelpers();
        InitEditing();
        InitTableEditor();
        InitObjectCode();
        InitCloseGuards();
        InitJournal();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            Controls.Common.Icons.IconFactory.SetDark(ActualThemeVariant == ThemeVariant.Dark);
            if (_sessionTree is null && _model.Roots.Count > 0)
            {
                await _model.ExpandAsync(_model.Roots[0]);
            }
            _ui.Refresh();
        };
        Closing += (_, _) => SaveWindowState();
        InitSessions();

        if (_sessionTree is null)
        {
            OpenSyntheticTable("synthetic_1m", SyntheticTreeProvider.SessionColors[0], 20261001);
        }
        foreach (var line in context.IgnoredShortcuts)
        {
            context.Log.Append(LogCategory.Info, $"Warning: ignored shortcut setting for \"{line}\"");
        }
        context.Log.Append(LogCategory.Info, $"Eunomia {ShellContext.Version} started");
        foreach (var warning in context.StartupWarnings)
        {
            context.Log.Append(LogCategory.Error, warning);
        }
        if (context.PreviousCrashReport is { } crash)
        {
            // Crash recovery (section 21.5): query tabs come back through the journal; the report stays on disk, without SQL or values.
            context.Log.Append(LogCategory.Error, $"Eunomia closed unexpectedly the last time. Query tabs were restored. Crash report: {crash}");
        }
        context.Diagnostics.Write(Eunomia.App.Diagnostics.DiagnosticLevel.Info, "app", $"Eunomia {ShellContext.Version} started");
        if (context.Diagnostics is not Eunomia.App.Diagnostics.NullDiagnosticLog)
        {
            _stalls = new UiStallMonitor(context.Diagnostics);
            Closed += (_, _) => _stalls.Dispose();
        }
        // The tab that was active before closing (query tab restore), otherwise the Data tab.
        _tabs.SelectedItem = _restoredFocus?.Item ?? _tabs.DataTab;
        _log.IsVisible &= !ShellTrace.Skip("log");
        _toolbar.IsVisible = !ShellTrace.Skip("toolbar");
        menu.IsVisible = !ShellTrace.Skip("menu");
        _dataGrid.IsVisible = !ShellTrace.Skip("grid");
        _tree.IsVisible = !ShellTrace.Skip("tree");
        ShellTrace.At("shell-ready");
    }

    public ShellContext Context => _context;

    public CommandUi CommandUi => _ui;

    public DataGridControl DataGrid => _dataGrid.Control;

    /// <summary>Box and label of the bottom filter panel (tests).</summary>
    internal TextBox LocalFilterBox => _filterBox;

    internal TextBlock LocalFilterInfo => _filterInfo;

    /// <summary>Data tab (tests).</summary>
    internal UI.Features.DataTab.DataTabView DataView => _dataView;

    public DatabaseTreeView Tree => _tree;

    public MainTabsView Tabs => _tabs;

    public SqlLogView LogView => _log;

    public Control Toolbar => _toolbar;

    public Border FilterPanel => _filterPanel;

    /// <summary>Editor of the active query tab (or of the first one).</summary>
    public SqlEditorControl SqlEditor => QueryEditorOf(_tabs.ActiveQueryTab ?? _tabs.QueryTabs[0]);

    /// <summary>Markers for the scroll benchmark (one below and one above the Data tab content).</summary>
    public Border RenderStartMarker { get; }

    public Border RenderEndMarker { get; }

    /// <summary>Current width of the tree and height of the log, in logical pixels.</summary>
    public (double TreeWidth, double LogHeight) PanelSizes => (_body.ColumnDefinitions[0].ActualWidth, _root.RowDefinitions[4].ActualHeight);

    /// <summary>Selects the Data tab (benchmarks).</summary>
    public void ShowDataTab() => _tabs.SelectedItem = _tabs.DataTab;

    /// <summary>
    /// The benchmarks' <c>--scale</c>: an approximation of a larger screen scale (the same number of pixels per cell),
    /// without changing the Windows scale.
    /// </summary>
    public void ApplyContentScale(double scale)
    {
        if (Content is Control content)
        {
            Content = null;
            Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = content };
        }
    }

    private static Border Marker() => new() { Width = 1, Height = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };

    private static Control Placeholder(string text) => new TextBlock
    {
        Text = text,
        Margin = new Thickness(12),
        Foreground = Brushes.Gray,
    };

    private static SqlEditorControl QueryEditorOf(QueryTab tab) => QueryViewOf(tab).Editor;

    private void OpenSyntheticTable(string name, uint sessionColor, ulong seed)
    {
        var source = new SyntheticRowSource(seed: seed);
        DataGrid.SessionColor = sessionColor;
        DataGrid.SetSource(source);
        _dataLabel.Text = $"{name}: {source.RowCount.Count.ToString("N0", CultureInfo.CurrentCulture)} rows total (exact)";
    }

    private void OnTreeFocusChanged()
    {
        UpdateQueryTargets();
        if (ActiveQueryView is { } activeQuery)
        {
            RefreshHelpers(activeQuery, onlyIfTableChanged: true);
        }
        var node = _tree.Tree.FocusedNode;
        if (node is null)
        {
            return;
        }
        Title = TreePath(node) + " - Eunomia " + ShellContext.Version;
        if (_sessionTree is not null)
        {
            if (_keepTableEditor)
            {
                // Cancel on the table editor question: the tree goes back to the table without reloading it.
                return;
            }
            if (!_restoringTreeNode && HasUnsavedObjectEditor && !IsEditedObject(node))
            {
                _ = LeaveObjectEditorThenNavigateAsync(node);
                return;
            }
            _ = NavigateAsync(node);
            SyncDiagramsWithTree(node);
        }
        else if (node.Kind == TreeNodeKind.Table && node.Parent is { } db)
        {
            var seed = StableHash(db.Name + "." + node.Name);
            OpenSyntheticTable($"{db.Name}.{node.Name}", node.Session?.SessionColor ?? 0, seed);
        }
    }

    private static string TreePath(TreeNode node)
    {
        var parts = new List<string>();
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n.Kind != TreeNodeKind.Group)
            {
                parts.Insert(0, n.Name);
            }
        }
        return string.Join("\\", parts);
    }

    /// <summary>Double click on an object with a query tab active inserts the name in the editor (FAM-T07); Shift qualifies it.</summary>
    private void OnTreeDoubleClicked(object? sender, TreeNodeDoubleClickEventArgs e)
    {
        if (!DoubleClickInsertsNodeText || _tabs.ActiveQueryTab is not { } tab)
        {
            return;
        }
        if (NodeInsertText(e.Node, e.Modifiers.HasFlag(KeyModifiers.Shift)) is not { } text)
        {
            return;
        }
        QueryEditorOf(tab).InsertAtCaret(text);
        e.Handled = true;
    }

    private void OnActiveTabChanged()
    {
        _ui.Refresh();
    }

    private static ulong StableHash(string text)
    {
        // FNV-1a: string.GetHashCode changes between processes.
        var h = 14695981039346656037UL;
        foreach (var c in text)
        {
            h = (h ^ c) * 1099511628211UL;
        }
        return h;
    }

    private void OnLogChanged(object? sender, LogChange change)
    {
        // The engine may write from any thread; the view only changes on the UI thread.
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyLogChange(change);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ApplyLogChange(change));
        }
    }

    private void ApplyLogChange(LogChange change)
    {
        if (change.Cleared)
        {
            ResetLogView();
            return;
        }
        _log.Append([.. change.Appended.Select(l => (l.Text, StyleOf(l.Kind)))], change.TrimmedFromStart);
    }

    private void ResetLogView()
    {
        var lines = _context.Log.Snapshot();
        _log.Reset(_context.Log.FirstLineNumber, [.. lines.Select(l => (l.Text, StyleOf(l.Kind)))]);
    }

    private static LogLineStyle StyleOf(LogLineKind kind) => kind switch
    {
        LogLineKind.Error => LogLineStyle.Error,
        LogLineKind.Warning => LogLineStyle.Warning,
        LogLineKind.Note => LogLineStyle.Note,
        LogLineKind.Info => LogLineStyle.Info,
        _ => LogLineStyle.Normal,
    };

    private void SetLogPanel(bool visible)
    {
        _context.Settings.Display.LogPanel = visible;
        _log.IsVisible = visible;
        _logSplitter.IsVisible = visible;
        _root.RowDefinitions[4].Height = visible ? new GridLength(_context.Settings.Panels.LogHeight) : new GridLength(0);
    }

    private void SetFilterPanel(bool visible)
    {
        _context.Settings.Display.FilterPanel = visible;
        _filterPanel.IsVisible = visible;
        if (visible)
        {
            _filterBox.Focus();
        }
    }

    private void RememberPanels()
    {
        var panels = _context.Settings.Panels;
        panels.TreeWidth = (int)Math.Round(_body.ColumnDefinitions[0].ActualWidth);
        if (_previewOn && _treeColumn.RowDefinitions[2].ActualHeight > 0)
        {
            panels.PreviewHeight = (int)Math.Round(_treeColumn.RowDefinitions[2].ActualHeight);
        }
        if (_context.Settings.Display.LogPanel)
        {
            panels.LogHeight = (int)Math.Round(_root.RowDefinitions[4].ActualHeight);
        }
    }

    /// <summary>Display › Reset panel dimensions (appendix C §1.3).</summary>
    public void ResetPanelDimensions()
    {
        var panels = _context.Settings.Panels;
        panels.Reset();
        _body.ColumnDefinitions[0].Width = new GridLength(panels.TreeWidth);
        if (_previewOn)
        {
            _treeColumn.RowDefinitions[2].Height = new GridLength(panels.PreviewHeight);
        }
        if (_context.Settings.Display.LogPanel)
        {
            _root.RowDefinitions[4].Height = new GridLength(panels.LogHeight);
        }
        foreach (var tab in _tabs.QueryTabs.Where(t => t.HasContent))
        {
            var grid = (Grid)tab.Content;
            grid.RowDefinitions[0].Height = new GridLength(panels.QueryEditorHeight);
            ((Grid)grid.Children[0]).ColumnDefinitions[2].Width = new GridLength(panels.QueryHelpersWidth);
        }
    }

    private void SaveWindowState()
    {
        var geometry = _context.Settings.MainWindow;
        geometry.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            geometry.Left = Position.X;
            geometry.Top = Position.Y;
            geometry.Width = (int)Math.Round(Width);
            geometry.Height = (int)Math.Round(Height);
        }
        RememberPanels();
        _context.SaveSettings();
    }

    private void ShowError(Exception ex)
    {
        _context.Log.Append(LogCategory.Error, ex.Message);
        _ = MessageDialog.ShowAsync(this, "Error", ex.Message, MessageDialogButtons.Ok);
    }

    private void AddShellStyles()
    {
        // Toolbar buttons as in HeidiSQL: icon only, 22 px, no background until hover.
        Styles.Add(new Style(x => x.OfType<Button>().Class("toolbar"))
        {
            Setters =
            {
                new Setter(PaddingProperty, new Thickness(3)),
                new Setter(MinHeightProperty, 22.0),
                new Setter(MinWidthProperty, 22.0),
                new Setter(BackgroundProperty, Brushes.Transparent),
                new Setter(BorderThicknessProperty, new Thickness(0)),
            },
        });
        Styles.Add(new Style(x => x.OfType<Button>().Class("toolbar-arrow"))
        {
            Setters =
            {
                new Setter(PaddingProperty, new Thickness(2, 0)),
                new Setter(MinHeightProperty, 22.0),
                new Setter(MinWidthProperty, 9.0),
                new Setter(BackgroundProperty, Brushes.Transparent),
                new Setter(BorderThicknessProperty, new Thickness(0)),
            },
        });
        // Disabled: just the dimmed icon, without Fluent's grey background (as in HeidiSQL).
        Styles.Add(new Style(x => x.OfType<Button>().Class("toolbar").Class(":disabled").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters = { new Setter(BackgroundProperty, Brushes.Transparent) },
        });
        // Toggle on: a "pressed" button.
        Styles.Add(new Style(x => x.OfType<Button>().Class("toolbar").Class("checked"))
        {
            Setters =
            {
                new Setter(BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x78, 0xD7))),
                new Setter(BorderThicknessProperty, new Thickness(1)),
                new Setter(BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0x78, 0xD7))),
            },
        });
        Styles.Add(new Style(x => x.OfType<Border>().Class("toolbar-separator"))
        {
            Setters = { new Setter(Border.BackgroundProperty, Brushes.Silver) },
        });
        Styles.Add(new Style(x => x.OfType<TabItem>())
        {
            Setters =
            {
                new Setter(FontSizeProperty, 12.0),
                new Setter(MinHeightProperty, 24.0),
                new Setter(PaddingProperty, new Thickness(8, 2)),
            },
        });
        Styles.Add(new Style(x => x.OfType<MenuItem>())
        {
            Setters = { new Setter(FontSizeProperty, 12.0), new Setter(MinHeightProperty, 22.0) },
        });
    }
}
