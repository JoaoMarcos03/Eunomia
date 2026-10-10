using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.App.Transfer;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Shell.Views;

/// <summary>
/// Run SQL file progress (appendix B §4.2: HeidiSQL's <c>IProgressDialog</c>): "Importing file X", "Processing
/// query #N (E Errors)", "Position in file: a / b. Affected rows: n.", a bar by the bytes of all the files, time and
/// estimate. Not modal: the session stays usable, the job runs on a job connection. Cancel stops the job; stopped by
/// an error or a cancel, Resume continues from the point saved in the journal.
/// </summary>
public sealed class SqlFileRunWindow : Window
{
    private readonly TextBlock _line1 = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _line2 = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _time = new() { Foreground = Brushes.Gray };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1000, Height = 16 };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 85, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
    private readonly Button _resume = new() { Content = "Resume", MinWidth = 85, HorizontalContentAlignment = HorizontalAlignment.Center, IsVisible = false };
    private readonly long _totalBytes;
    private CancellationTokenSource _cts = new();
    private long _doneBytes;

    public SqlFileRunWindow(long totalBytes)
    {
        Icon = BrandAssets.WindowIcon;
        _totalBytes = Math.Max(1, totalBytes);
        Title = "Importing file";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiFonts.Ui;
        FontSize = 12;

        _cancel.Click += (_, _) =>
        {
            if (Running)
            {
                _cts.Cancel();
                _line1.Text = "Cancelling...";
            }
            else
            {
                Close();
            }
        };
        _resume.Click += (_, _) => ResumeRequested?.Invoke(this, EventArgs.Empty);
        Closing += (_, _) => _cts.Cancel();

        Avalonia.Automation.AutomationProperties.SetName(_bar, "Import progress");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(_resume);
        buttons.Children.Add(_cancel);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
        panel.Children.Add(_line1);
        panel.Children.Add(_line2);
        panel.Children.Add(_bar);
        panel.Children.Add(_time);
        panel.Children.Add(buttons);
        Content = panel;
    }

    /// <summary>The job in progress stops with this token (a new one for each file or resume).</summary>
    public CancellationToken Token => _cts.Token;

    public bool Running { get; private set; }

    /// <summary>Resume was requested (the shell runs the resume and calls <see cref="Start"/> again).</summary>
    public event EventHandler? ResumeRequested;

    internal string Line1 => _line1.Text ?? string.Empty;

    internal string Line2 => _line2.Text ?? string.Empty;

    internal Button ResumeButton => _resume;

    internal Button CancelButton => _cancel;

    /// <summary>Starts a file; <paramref name="doneBytes"/> are the bytes of the previous files.</summary>
    public void Start(string path, long doneBytes)
    {
        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
        _doneBytes = doneBytes;
        Running = true;
        Title = "Importing file " + Path.GetFileName(path);
        _cancel.Content = "Cancel";
        _resume.IsVisible = false;
        _line1.Text = "Opening " + path;
        _line2.Text = string.Empty;
    }

    public void Show(SqlFileProgress progress)
    {
        var c = CultureInfo.CurrentCulture;
        _line1.Text = string.Create(c, $"Processing query #{progress.Statements + 1:N0} ({progress.Errors:N0} Errors)");
        _line2.Text = string.Create(c, $"Position in file: {LargeFilesDialog.FormatSize(progress.BytesRead)} / {LargeFilesDialog.FormatSize(progress.TotalBytes)}. Affected rows: {progress.AffectedRows:N0}.");
        var done = _doneBytes + progress.BytesRead;
        _bar.Value = Math.Clamp(done * 1000.0 / _totalBytes, 0, 1000);
        var elapsed = progress.Elapsed;
        var eta = progress.BytesRead > 0 && progress.TotalBytes > progress.BytesRead
            ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (progress.TotalBytes - progress.BytesRead) / progress.BytesRead)
            : TimeSpan.Zero;
        _time.Text = string.Create(c, $"Elapsed {elapsed:hh\\:mm\\:ss}, remaining about {eta:hh\\:mm\\:ss}");
    }

    /// <summary>End: the final message; Resume shows when the job was left stopped with a journal.</summary>
    public void Finish(string message, bool canResume)
    {
        Running = false;
        _line1.Text = message;
        _cancel.Content = "Close";
        _resume.IsVisible = canResume;
    }
}
