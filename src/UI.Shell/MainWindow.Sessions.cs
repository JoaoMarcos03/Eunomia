using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Eunomia.App.Commands;
using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Results;
using Eunomia.Engine.Sql.Splitting;
using Eunomia.Presentation.Logging;
using Eunomia.Presentation.Sessions;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Tree;
using Eunomia.UI.Features.Sessions;
using Eunomia.UI.Shell.Sessions;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Sessions in the shell (phase 3; FAM-S01, S07, L06): Session Manager at startup, connecting (login, SSH host key), one tree
/// root per open session, Disconnect, "Connect to ▸" and the status bar. A minimal Data tab and Run on the real
/// connection until phases 5 and 6.
/// </summary>
public sealed partial class MainWindow
{
    private readonly SessionTreeProvider? _sessionTree;
    private readonly Dictionary<Guid, TreeNode> _sessionNodes = [];
    private SessionManagerModel? _managerModel;
    private DispatcherTimer? _statusTimer;
    private DateTimeOffset _lastServerPoll = DateTimeOffset.MinValue;

    /// <summary>Exports and imports settings files (the composition root wires it to Infra.Settings).</summary>
    public Func<string, string>? ImportSettingsFile { get; init; }

    /// <summary>Imports the settings of the HeidiSQL installed on this computer and returns the summary; <c>null</c> without HeidiSQL.</summary>
    public Func<string>? ImportHeidiSqlRegistry { get; init; }

    public Func<string, string>? ExportSettingsFile { get; init; }

    private ShellSessions? Sessions => _context.Sessions;

    /// <summary>Session of the node focused in the tree (the active session, FAM-S07).</summary>
    public OpenSession? ActiveSession =>
        _tree.Tree.FocusedNode?.Session?.Tag is Guid id ? Sessions?.Connector.Find(id) : null;

    private void InitSessions()
    {
        if (Sessions is not { } sessions)
        {
            return;
        }
        _managerModel = new SessionManagerModel(sessions.Catalog, sessions.Store, sessions.Secrets);
        sessions.Connector.SessionOpened += (_, open) => Dispatcher.UIThread.Post(() => OnSessionOpened(open));
        sessions.Connector.SessionClosed += (_, closed) => Dispatcher.UIThread.Post(() => OnSessionClosed(closed));

        var c = _context.Commands;
        c.SetHandler("session.manager", _ => new ValueTask(ShowSessionManagerAsync(startup: false)));
        c.SetHandler("session.disconnect", _ => new ValueTask(DisconnectAsync(ActiveSession!)), () => ActiveSession is not null);
        c.SetHandler("session.properties", _ => new ValueTask(ShowPropertiesAsync(ActiveSession!)), () => ActiveSession is not null);
        c.SetHandler("settings.import", _ => new ValueTask(ImportSettingsAsync(this)), () => ImportSettingsFile is not null);
        c.SetHandler("settings.export", _ => new ValueTask(ExportSettingsAsync(this)), () => ExportSettingsFile is not null);

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatusBar());
        _statusTimer.Start();
        _tabs.SelectionChanged += (_, _) =>
        {
            if (ReferenceEquals(_tabs.SelectedItem, _tabs.DataTab))
            {
                LoadDataForFocusedNode();
            }
            else if (ReferenceEquals(_tabs.SelectedItem, _tabs.TableTab))
            {
                LoadObjectEditor();
            }
        };
        // StartupRequest is init-only: it is only available after the constructor.
        Opened += async (_, _) =>
        {
            if (StartupRequest is { IsEmpty: false } request)
            {
                await StartFromCommandLineAsync(request);
            }
            else if (sessions.ShowManagerOnStartup && !await ReconnectLastSessionsAsync())
            {
                await OfferHeidiSqlImportAsync();
                await ShowSessionManagerAsync(startup: true);
            }
        };
        Closing += (_, _) =>
        {
            _context.Settings.SessionManager.LastSessions = [.. sessions.Connector.Open.Select(o => o.Id)];
            _context.SaveSettings();
        };
        Closed += async (_, _) => await sessions.Connector.DisconnectAllAsync();
    }

    /// <summary>
    /// Preferences › General › "Automatically reconnect to previously opened sessions on startup": connects the sessions that
    /// were open at shutdown; returns whether it connected any (with none, the Session Manager shows).
    /// </summary>
    internal async Task<bool> ReconnectLastSessionsAsync()
    {
        var options = _context.Settings.SessionManager;
        if (!options.AutoReconnect || Sessions is not { } sessions)
        {
            return false;
        }
        var any = false;
        foreach (var id in options.LastSessions.ToList())
        {
            if (sessions.Catalog.Sessions.FirstOrDefault(s => s.Id == id) is { } definition)
            {
                any |= await ConnectAsync(this, definition, null);
            }
        }
        return any;
    }

    /// <summary>Startup command line (FAM-S06): connects the requested session instead of showing the Session Manager.</summary>
    public CommandLineRequest? StartupRequest { get; init; }

    /// <summary>Connects without dialogs (measurements and automation); returns the error or <c>null</c>.</summary>
    public async Task<string?> ConnectQuietAsync(SessionDefinition values, LoginCredentials? credentials)
    {
        try
        {
            var open = await Sessions!.Connector.ConnectAsync(values, credentials, CancellationToken.None);
            FocusSessionNode(open.Id);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
            return ex.Message;
        }
    }

    private async Task StartFromCommandLineAsync(CommandLineRequest request)
    {
        foreach (var file in request.SqlFiles)
        {
            try
            {
                await LoadFileAsync(file, Eunomia.App.Query.SqlFile.AutoDetect);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await MessageDialog.ShowAsync(this, "Error", ex.Message, MessageDialogButtons.Ok);
            }
        }
        var session = CommandLine.Resolve(request, Sessions!.Catalog);
        if (session is null)
        {
            await ShowSessionManagerAsync(startup: true);
            return;
        }
        var credentials = request.Password is not null ? new LoginCredentials(session.User, request.Password) : null;
        if (!await ConnectAsync(this, session, credentials) && Sessions.Connector.Open.Count == 0)
        {
            await ShowSessionManagerAsync(startup: true);
        }
    }

    /// <summary>Modal Session Manager; at startup, Cancel closes the application (as in HeidiSQL).</summary>
    public async Task ShowSessionManagerAsync(bool startup)
    {
        if (Sessions is not { } sessions || _managerModel is null)
        {
            return;
        }
        var window = new SessionManagerWindow(new SessionManagerServices
        {
            Model = _managerModel,
            Options = _context.Settings.SessionManager,
            OpenAsync = (owner, values, credentials) => ConnectAsync(owner, values, credentials),
            ImportSettingsAsync = ImportSettingsFile is null ? null : owner => ImportSettingsAsync(owner),
            ImportHeidiSqlAsync = ImportHeidiSqlRegistry is null ? null : owner => ImportHeidiSqlAsync(owner, ask: false),
            ExportSettingsAsync = ExportSettingsFile is null ? null : owner => ExportSettingsAsync(owner),
            ShowPreferencesAsync = owner => new PreferencesWindowLauncher(_context).ShowAsync(owner),
            ShowAboutAsync = owner => AboutDialog.ShowAsync(owner, ShellContext.Version),
            ListDatabasesAsync = (values, credentials) => sessions.Connector.ListDatabasesAsync(values, credentials, CancellationToken.None),
            Version = ShellContext.Version,
        });
        var opened = await window.ShowDialog<bool>(this);
        _context.SaveSettings();
        if (startup && !opened && sessions.Connector.Open.Count == 0)
        {
            Close();
        }
    }

    /// <summary>
    /// Connects a session and focuses it in the tree: asks for credentials if needed, confirms new SSH host keys (and blocks
    /// changed keys) and shows the errors; <paramref name="showHostTab"/> shows the Host tab (as in HeidiSQL). Returns whether
    /// it connected.
    /// </summary>
    public async Task<bool> ConnectAsync(Window owner, SessionDefinition values, LoginCredentials? credentials, bool showHostTab = true)
    {
        var sessions = Sessions ?? throw new InvalidOperationException("No session services.");
        var connector = sessions.Connector;
        if (values.LoginPrompt || (credentials is null && connector.NeedsCredentials(values)))
        {
            credentials = await LoginDialog.ShowAsync(owner, values.Path, values.User);
            if (credentials is null)
            {
                return false;
            }
        }
        _context.Status.Busy = $"Connecting to {values.Host} ...";
        try
        {
            while (true)
            {
                try
                {
                    var open = await connector.ConnectAsync(values, credentials, CancellationToken.None);
                    FocusSessionNode(open.Id, showHostTab);
                    return true;
                }
                catch (SshHostKeyException hostKey) when (hostKey.Problem is SshHostKeyProblem.Unknown or SshHostKeyProblem.NewKeyType && sessions.Ssh is not null)
                {
                    var answer = await MessageDialog.ShowAsync(owner, "SSH host key",
                        hostKey.Message + "\n\nDo you trust this host and want to continue? The key will be remembered.", MessageDialogButtons.YesNo);
                    if (answer != MessageDialogResult.Yes)
                    {
                        return false;
                    }
                    sessions.Ssh.TrustHostKey(hostKey);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
            await MessageDialog.ShowAsync(owner, "Error", ConnectErrorText(values, ex), MessageDialogButtons.Ok);
            return false;
        }
        finally
        {
            sessions.Store.Save(sessions.Catalog);
            _context.Status.SetIdle();
        }
    }

    private static string ConnectErrorText(SessionDefinition values, Exception ex) =>
        ex is SessionConnectionLostException or SessionOperationException
            ? $"Could not connect to \"{values.Path}\".\n\n{ex.Message}"
            : $"Could not connect to \"{values.Path}\".\n\n{ex.Message}\n({ex.GetType().Name})";

    private void OnSessionOpened(OpenSession open)
    {
        if (_sessionNodes.TryGetValue(open.Id, out var existing))
        {
            _model.RemoveRoot(existing);
        }
        var node = new TreeNode(TreeNodeKind.Session, open.Name)
        {
            Tag = open.Id,
            SessionColor = open.Color is { } rgb ? 0xFF000000 | rgb : 0,
        };
        _sessionNodes[open.Id] = node;
        AttachTree(open, node);
        _model.AddRoot(node);
        WatchTransactions(open);
        open.Host.StateChanged += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            node.IsGhosted = e.State is SessionState.Reconnecting or SessionState.Failed or SessionState.Closed;
            _tree.Tree.InvalidateVisual();
            UpdateStatusBar();
            _ui.Refresh();
        });
        _ = ExpandSessionAsync(node);
        UpdateStatusBar();
        _ui.Refresh();
    }

    private async Task ExpandSessionAsync(TreeNode node)
    {
        try
        {
            await _model.ExpandAsync(node);
            var last = _context.Settings.SessionManager.RestoreLastUsedDatabase ? ActiveSession?.Definition.LastUsedDatabase : null;
            if (!string.IsNullOrEmpty(last) && node.Children?.FirstOrDefault(c => c.Name == last) is { } db)
            {
                // RestoreLastUsedDB: the last database used is selected and opened (appendix D A7).
                _tree.Tree.FocusedNode = db;
                await _model.ExpandAsync(db);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
        }
    }

    private void OnSessionClosed(OpenSession closed)
    {
        if (_sessionNodes.Remove(closed.Id, out var node))
        {
            _model.RemoveRoot(node);
        }
        UpdateStatusBar();
        _ui.Refresh();
    }

    private void FocusSessionNode(Guid id, bool showHostTab = true)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_sessionNodes.TryGetValue(id, out var node))
            {
                _tree.Tree.FocusedNode = node;
                if (showHostTab)
                {
                    _tabs.SelectedItem = _tabs.HostTab;
                }
            }
        });
    }

    /// <summary>Disconnect: closes the session; with no sessions left, the Session Manager comes back (Cancel there closes the application).</summary>
    public async Task DisconnectAsync(OpenSession session)
    {
        if (!await ResolveOpenTransactionAsync(session))
        {
            return;
        }
        await Sessions!.Connector.DisconnectAsync(session);
        if (Sessions.Connector.Open.Count == 0)
        {
            await ShowSessionManagerAsync(startup: true);
        }
    }

    /// <summary>Dynamic list of "Connect to ▸" and of the Session manager button drop-down (connected sessions with a check).</summary>
    internal IReadOnlyList<object> SessionMenuItems()
    {
        if (Sessions is not { } sessions)
        {
            return [];
        }
        var items = new List<object>();
        foreach (var s in sessions.Catalog.Sessions.OrderBy(s => s.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            var connected = sessions.Connector.Find(s.Id) is not null;
            var item = new MenuItem { Header = LiteralText.Escaped(s.Path), ToggleType = MenuItemToggleType.CheckBox, IsChecked = connected };
            item.Click += async (_, _) =>
            {
                if (sessions.Connector.Find(s.Id) is { } open)
                {
                    FocusSessionNode(open.Id);
                }
                else
                {
                    await ConnectAsync(this, s, null);
                }
            };
            items.Add(item);
        }
        return items;
    }

    private async Task ImportSettingsAsync(Window owner)
    {
        var files = await PathPicker.OpenFilesAsync(owner, new FilePickerOpenOptions
        {
            Title = "Import settings file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Settings files") { Patterns = ["*.txt", "*.json"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0 || ImportSettingsFile is null)
        {
            return;
        }
        var path = files[0];
        try
        {
            var summary = ImportSettingsFile(path);
            _context.Log.Append(LogCategory.Info, summary.Replace('\n', ' '));
            _context.Commands.ImportShortcutOverrides(_context.Settings.Shortcuts);
            ApplyImportedSettings();
            await MessageDialog.ShowAsync(owner, "Import settings file", summary, MessageDialogButtons.Ok);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            await MessageDialog.ShowAsync(owner, "Error", ex.Message, MessageDialogButtons.Ok);
        }
    }

    /// <summary>
    /// After importing from HeidiSQL: applies right away the theme, the tree width, the panels, the log and the editor and grid
    /// options (the rest, such as tabs on several rows, waits for the next start).
    /// </summary>
    internal void ApplyImportedSettings()
    {
        var s = _context.Settings;
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = Views.PreferencesWindow.ThemeOf(s.Theme);
        }
        var log = _context.Log.Options;
        if (!ReferenceEquals(log, s.Log))
        {
            log.MaxLines = s.Log.MaxLines;
            log.MaxLineLength = s.Log.MaxLineLength;
            log.Errors = s.Log.Errors;
            log.UserSql = s.Log.UserSql;
            log.InternalSql = s.Log.InternalSql;
            log.Script = s.Log.Script;
            log.Info = s.Log.Info;
            log.Debug = s.Log.Debug;
            log.Timestamps = s.Log.Timestamps;
            log.HorizontalScrollbar = s.Log.HorizontalScrollbar;
        }
        _log.HorizontalScrollbar = s.Log.HorizontalScrollbar;
        _body.ColumnDefinitions[0].Width = new GridLength(Math.Max(40, s.Panels.TreeWidth));
        SetLogPanel(s.Display.LogPanel);
        _filterPanel.IsVisible = s.Display.FilterPanel;
        _tree.FiltersVisible = s.Display.TreeFilters;
        _tree.Tree.ShowSizes = s.Display.ObjectSizes;
        ApplyEditorOptionsToTabs();
        _ = RebuildTreeLayoutAsync();
        _ui.Refresh();
    }

    /// <summary>Replaces the question and the summary of the HeidiSQL import (tests).</summary>
    internal Func<Task<bool>>? HeidiImportQuestionOverride { get; set; }

    /// <summary>
    /// First start without sessions and with HeidiSQL installed: asks once whether to import its sessions (someone switching
    /// from HeidiSQL gets their usual connections right away).
    /// </summary>
    internal async Task OfferHeidiSqlImportAsync()
    {
        var options = _context.Settings.SessionManager;
        if (ImportHeidiSqlRegistry is null || Sessions is not { Catalog.Sessions.Count: 0 } || options.HeidiImportOffered)
        {
            return;
        }
        options.HeidiImportOffered = true;
        _context.SaveSettings();
        await ImportHeidiSqlAsync(this, ask: true);
    }

    /// <summary>Import sessions from HeidiSQL: the sessions, folders, shortcuts and favourites from HeidiSQL's registry.</summary>
    private async Task ImportHeidiSqlAsync(Window owner, bool ask)
    {
        if (ImportHeidiSqlRegistry is not { } import)
        {
            return;
        }
        if (ask && HeidiImportQuestionOverride is { } question)
        {
            if (!await question())
            {
                return;
            }
        }
        else if (ask && await MessageDialog.ShowAsync(owner, "Import from HeidiSQL",
                "HeidiSQL sessions were found on this computer. Import them now?\n\nSessions with their folders and colours, favourites, shortcuts, preferences, the saved filters and sorting of each table and the query history are copied; passwords go to the Windows Credential Manager. HeidiSQL is not changed.",
                MessageDialogButtons.YesNo) != MessageDialogResult.Yes)
        {
            return;
        }
        try
        {
            var summary = import();
            _context.Log.Append(LogCategory.Info, summary.Replace('\n', ' '));
            _context.Commands.ImportShortcutOverrides(_context.Settings.Shortcuts);
            ApplyImportedSettings();
            if (HeidiImportQuestionOverride is null)
            {
                await MessageDialog.ShowAsync(owner, "Import from HeidiSQL", summary, MessageDialogButtons.Ok);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Security.SecurityException)
        {
            await MessageDialog.ShowAsync(owner, "Error", ex.Message, MessageDialogButtons.Ok);
        }
    }

    private async Task ExportSettingsAsync(Window owner)
    {
        var path = await PathPicker.SaveFileAsync(owner, new FilePickerSaveOptions
        {
            Title = "Export settings file",
            SuggestedFileName = "eunomia-settings.json",
            FileTypeChoices = [new FilePickerFileType("Settings files") { Patterns = ["*.json"] }],
        });
        if (path is null || ExportSettingsFile is null)
        {
            return;
        }
        var summary = ExportSettingsFile(path);
        await MessageDialog.ShowAsync(owner, "Export settings file", summary, MessageDialogButtons.Ok);
    }

    private async Task ShowPropertiesAsync(OpenSession session)
    {
        var host = session.Host;
        var lines = new List<string>
        {
            $"Session: {session.Name}",
            $"Host: {session.Definition.Host}:{session.Definition.Port}",
            $"Server: {host.Server?.VersionText} {host.Server?.VersionComment}",
            $"State: {host.State}",
            $"Thread-ID: {host.PrimaryConnectionId}",
            $"Connected since: {host.ConnectedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            $"SSH tunnel: {(session.Definition.Ssh.Active ? session.Definition.Ssh.Host : "no")}",
            $"SSL: {(session.Definition.Ssl.Active ? "yes" : "preferred")}",
        };
        await MessageDialog.ShowAsync(this, "Connection properties", string.Join('\n', lines), MessageDialogButtons.Ok);
    }

    /// <summary>Status bar (FAM-L06): Connected (1 s), server, Uptime and Server time (20 s), busy.</summary>
    private void UpdateStatusBar()
    {
        var status = _context.Status;
        if (ActiveSession is not { } session || session.Host.State is SessionState.Closed or SessionState.Failed)
        {
            if (_sessionTree is not null)
            {
                status.ShowDisconnected();
            }
            return;
        }
        var host = session.Host;
        var connectedFor = host.ConnectedAt is { } at ? DateTimeOffset.UtcNow - at : TimeSpan.Zero;
        status.Connected = host.State == SessionState.Reconnecting ? "Reconnecting ..." : "Connected: " + Presentation.Shell.DurationFormatter.Format(connectedFor);
        status.Transaction = TransactionStatus(session);
        status.Server = host.Server is { } server ? $"{(server.Engine == DbEngine.MariaDb ? "MariaDB" : "MySQL")} {server.Version}" : string.Empty;
        status.ServerTooltip = host.Server is { } s ? $"Server version: {s.VersionText}\nVersion comment: {s.VersionComment}\nThread-ID: {host.PrimaryConnectionId}{EncryptionLine(host)}" : null;
        if (!status.IsBusy && host.IsPrimaryBusy)
        {
            status.Busy = "Executing query ...";
        }
        if (DateTimeOffset.UtcNow - _lastServerPoll > TimeSpan.FromSeconds(20))
        {
            _lastServerPoll = DateTimeOffset.UtcNow;
            _ = PollServerClockAsync(session);
        }
    }

    /// <summary>"Encryption" line of the server tooltip (23.2: a connection without TLS is made visible).</summary>
    private static string EncryptionLine(SessionHost host) => host.Encryption switch
    {
        null => string.Empty,
        "" when host.ThroughSsh => "\nEncryption: SSH tunnel (no TLS to the database server)",
        "" => "\nEncryption: none (no TLS)",
        var cipher => "\nEncryption: TLS (" + cipher + ")" + (host.ThroughSsh ? " through an SSH tunnel" : string.Empty),
    };

    private async Task PollServerClockAsync(OpenSession session)
    {
        try
        {
            // One round trip, and SHOW STATUS exists in every version (information_schema.GLOBAL_STATUS does not exist on
            // MySQL 8: the uptime and the server time were left blank).
            var (uptime, text) = await session.Host.RunMetadataAsync(async (h, ct) =>
            {
                string? up = null, time = null;
                var options = new ExecuteOptions(BlockSize: 16) { CollectWarnings = false };
                await foreach (var e in h.Executor.ExecuteAsync(SqlBatch.Single("SELECT NOW();\nSHOW GLOBAL STATUS LIKE 'Uptime'"), options, ct).ConfigureAwait(false))
                {
                    switch (e)
                    {
                        case RowBlockReceived { Block.RowCount: > 0 } block when block.ResultSetIndex == 0:
                            time = DisplayFormatter.FormatRaw(block.Block, 0, 0);
                            break;
                        case RowBlockReceived { Block.RowCount: > 0 } block when block.ResultSetIndex == 1 && block.Block.Schema.Count > 1:
                            up = DisplayFormatter.FormatRaw(block.Block, 0, 1);
                            break;
                    }
                }
                return (up, time);
            }, CancellationToken.None, WorkPriority.Statistics);
            var seconds = double.TryParse(uptime, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (double?)null;
            var now = DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : (DateTime?)null;
            var serverNow = now is { } dt ? new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt)) : (DateTimeOffset?)null;
            if (now is { } wall)
            {
                // Server clock for Insert value ▸ (datetime, date, time, year).
                _serverClockOffsets[session.Id] = wall - DateTime.Now;
            }
            var connectedFor = session.Host.ConnectedAt is { } at ? DateTimeOffset.UtcNow - at : TimeSpan.Zero;
            _context.Status.ShowConnection(connectedFor, seconds is { } s2 ? TimeSpan.FromSeconds(s2) : null, serverNow);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Status.Uptime = "Uptime: unknown";
        }
    }

    /// <summary>Opens Preferences from the Session Manager.</summary>
    private sealed class PreferencesWindowLauncher(ShellContext context)
    {
        public async Task ShowAsync(Window owner) => await new Views.PreferencesWindow(context).ShowDialog(owner);
    }
}
