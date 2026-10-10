using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.Presentation.Shell;
using Eunomia.UI.Controls.Common.Icons;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Shell.Views;

/// <summary>
/// 7-panel status bar (section 5.3, appendix C §1.6, FAM-L06), 19 px. The widths of panels 1–6 follow HeidiSQL's
/// rule: sample text + icon + 30 px, with a cap as a percentage of the window; panel 0 takes the
/// rest. The progress bar is drawn on top of panel 5. Panel 3 (server) is clickable.
/// </summary>
public sealed class StatusBarView : Border
{
    // (sample text, cap as a fraction of the width, icon)
    private static readonly (string Sample, double Cap, string? Icon)[] Panels =
    [
        (string.Empty, 1, null),
        ("r10 : c10 (10 KiB)", 0.12, null),
        ("Connected: 13 days, 00:00 h", 0.12, "session-manager"),
        ("MariaDB or MySQL 5.7.6", 0.12, "database"),
        ("Uptime: 130 days, 00:00 h", 0.12, null),
        ("Server time: 20:00 PM", 0.12, "event"),
        ("Executing query #1 of 2 ...", 0.20, "idle"),
    ];

    private readonly StatusBarModel _model;
    private readonly Grid _grid = new();
    private readonly TextBlock[] _texts = new TextBlock[7];
    private readonly Border[] _panels = new Border[7];
    private string? _panel2Token;
    private readonly Image _busyIcon = new() { Width = 16, Height = 16 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, IsVisible = false, MinHeight = 0, Height = 15, VerticalAlignment = VerticalAlignment.Center };

    public StatusBarView(StatusBarModel model)
    {
        _model = model;
        Height = 19;
        Themed.Bind(this, BackgroundProperty, "surface.panel");
        for (var i = 0; i < Panels.Length; i++)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition(i == 0 ? GridLength.Star : new GridLength(100)));
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Margin = new Thickness(4, 0) };
            Themed.Bind(text, TextBlock.ForegroundProperty, "text.primary");
            _texts[i] = text;
            var cell = new DockPanel { LastChildFill = true };
            if (i == 6)
            {
                DockPanel.SetDock(_busyIcon, Dock.Left);
                _busyIcon.Margin = new Thickness(3, 0, 0, 0);
                cell.Children.Add(_busyIcon);
            }
            else if (Panels[i].Icon is { } icon && IconFactory.CreateImage(icon) is { } image)
            {
                image.Margin = new Thickness(3, 0, 0, 0);
                DockPanel.SetDock(image, Dock.Left);
                cell.Children.Add(image);
            }
            cell.Children.Add(text);
            var panel = new Border { Child = cell, BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), Name = "status" + i };
            _panels[i] = panel;
            Themed.Bind(panel, Border.BorderBrushProperty, "border");
            if (i == 5)
            {
                var overlay = new Panel();
                overlay.Children.Add(panel);
                overlay.Children.Add(_progress);
                Grid.SetColumn(overlay, i);
                _grid.Children.Add(overlay);
            }
            else
            {
                Grid.SetColumn(panel, i);
                _grid.Children.Add(panel);
            }
        }
        _texts[3].Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        _texts[3].PointerPressed += (_, _) => ServerPanelClicked?.Invoke(this, EventArgs.Empty);
        Child = _grid;
        _model.PropertyChanged += OnModelChanged;
        Update();
    }

    /// <summary>Click on panel 3: Connection properties.</summary>
    public event EventHandler? ServerPanelClicked;

    public string PanelText(int index) => _texts[index].Text ?? string.Empty;

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Widths as in HeidiSQL's FormResize: sample + icon + 30 px, capped at a fraction of the window.
        for (var i = 1; i < Panels.Length; i++)
        {
            var sample = new FormattedText(Panels[i].Sample, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(_texts[i].FontFamily), _texts[i].FontSize, Brushes.Black);
            var width = sample.Width + (Panels[i].Icon is null && i != 6 ? 0 : 16) + 30;
            _grid.ColumnDefinitions[i].Width = new GridLength(Math.Min(width, finalSize.Width * Panels[i].Cap));
        }
        return base.ArrangeOverride(finalSize);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        _texts[0].Text = _model.Info;
        _texts[1].Text = _model.Position;
        // Open transaction: panel 2 shows it with the background of the SQL log's warning lines.
        _texts[2].Text = _model.Transaction.Length > 0 ? _model.Transaction : _model.Connected;
        var token = _model.Transaction.Length == 0 ? null : _model.HasUncommittedChanges ? "log.warning" : "log.note";
        if (token != _panel2Token)
        {
            _panel2Token = token;
            if (token is null)
            {
                _panels[2].Background = null;
            }
            else
            {
                Themed.Bind(_panels[2], Border.BackgroundProperty, token);
            }
            ToolTip.SetTip(_texts[2], token is null ? null : "Statements in this session run inside a transaction: COMMIT to save the changes or ROLLBACK to discard them.");
        }
        _texts[3].Text = _model.Server;
        ToolTip.SetTip(_texts[3], _model.ServerTooltip);
        _texts[4].Text = _model.Uptime;
        _texts[5].Text = _model.ServerTime;
        _texts[6].Text = _model.Busy;
        _busyIcon.Source = IconFactory.Get(_model.IsBusy ? "busy" : "idle");
        _progress.IsVisible = _model.Progress is not null;
        _progress.Value = _model.Progress ?? 0;
    }
}
