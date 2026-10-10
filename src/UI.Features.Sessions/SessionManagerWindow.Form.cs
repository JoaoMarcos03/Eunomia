using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Eunomia.App.Sessions;
using Eunomia.Presentation.Sessions;
using Eunomia.UI.Controls.Common.Accessibility;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Text;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Features.Sessions;

/// <summary>Campos dos tabs do Session Manager (anexo A §1.6), com os rótulos do HeidiSQL.</summary>
public sealed partial class SessionManagerWindow
{
    /// <summary>Cores de sessão: as observadas nos screenshots (secção 5.5) e algumas standard.</summary>
    private static readonly (string Name, uint? Rgb)[] ColorPresets =
    [
        ("None", null), ("Green", 0x006400), ("Crimson", 0xDC143C), ("Red", 0xF03333), ("Olive", 0x76845C), ("Dark yellow", 0x9D850B),
        ("Navy", 0x000080), ("Blue", 0x1E90FF), ("Teal", 0x008080), ("Purple", 0x800080), ("Orange", 0xFF8C00), ("Gray", 0x808080),
    ];

    private bool _loading;
    private TextBlock _startText = null!;

    // Settings.
    private ComboBox _netType = null!;
    private TextBox _host = null!;
    private CheckBox _loginPrompt = null!;
    private CheckBox _windowsAuth = null!;
    private TextBox _user = null!;
    private TextBox _password = null!;
    private NumericUpDown _port = null!;
    private CheckBox _compressed = null!;
    private TextBox _databases = null!;
    private TextBox _comment = null!;

    // SSH.
    private CheckBox _sshActive = null!;
    private TextBox _sshHost = null!;
    private NumericUpDown _sshPort = null!;
    private TextBox _sshUser = null!;
    private TextBox _sshPassword = null!;
    private NumericUpDown _sshTimeout = null!;
    private TextBox _sshKey = null!;
    private NumericUpDown _sshLocalPort = null!;
    private CheckBox _sshAgent = null!;

    // Advanced.
    private TextBox _startup = null!;
    private NumericUpDown _queryTimeout = null!;
    private NumericUpDown _keepAlive = null!;
    private CheckBox _localTimeZone = null!;
    private CheckBox _fullStatus = null!;
    private CheckBox _cleartext = null!;
    private CheckBox _forceUnicode = null!;
    private ComboBox _colorPreset = null!;
    private TextBox _colorHex = null!;
    private Border _colorSwatch = null!;
    private TextBox _hidePattern = null!;
    private TextBox _logPath = null!;
    private CheckBox _logDdl = null!;
    private CheckBox _logDml = null!;
    private CheckBox _readOnly = null!;
    private CheckBox _confirmChanges = null!;
    private CheckBox _metadataConnection = null!;
    private NumericUpDown _jobs = null!;

    // SSL.
    private CheckBox _sslActive = null!;
    private TextBox _sslKey = null!;
    private TextBox _sslCa = null!;
    private TextBox _sslCert = null!;
    private TextBox _sslCipher = null!;
    private ComboBox _sslVerify = null!;

    // Statistics.
    private TextBlock _statCreated = null!;
    private TextBlock _statLast = null!;
    private TextBlock _statOk = null!;
    private TextBlock _statFailed = null!;

    private Control BuildStartTab()
    {
        _startText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var import = new Button { Content = "Import settings file ...", IsEnabled = _services.ImportSettingsAsync is not null };
        import.Click += async (_, _) => await ImportAsync();
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
        panel.Children.Add(BuildBrandHeader());
        panel.Children.Add(_startText);
        if (_services.ImportHeidiSqlAsync is not null)
        {
            // Quem vem do HeidiSQL: as sessões dele, sem exportar primeiro.
            var heidi = new Button { Content = "Import sessions from HeidiSQL", Name = "btnImportHeidi" };
            heidi.Click += async (_, _) => await ImportHeidiSqlAsync();
            panel.Children.Add(heidi);
        }
        panel.Children.Add(import);
        return panel;
    }

    /// <summary>Logo e versão no topo do Start tab.</summary>
    private Control BuildBrandHeader()
    {
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        title.Children.Add(new TextBlock { Text = "Eunomia", FontSize = 20, FontWeight = FontWeight.SemiBold });
        if (_services.Version.Length > 0)
        {
            title.Children.Add(Themed.Bind(new TextBlock { Text = "Version " + _services.Version }, TextBlock.ForegroundProperty, "text.muted"));
        }
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new Image { Source = BrandAssets.Logo, Width = 56, Height = 56, Name = "imgLogo" });
        header.Children.Add(title);
        return header;
    }

    private void UpdateStartText() =>
        _startText.Text = _model.Catalog.Sessions.Count == 0
            ? "New here? In order to connect to a server, you have to create a so called \"session\" at first. Just click the \"New\" button on the bottom left to create your first session. Give it a friendly name (e.g. \"Local DB server\") so you'll recall it the next time you start Eunomia."
            : "Please click a session on the left list to edit parameters, doubleclick to open it.";

    private Control BuildSettingsTab()
    {
        var f = new FormBuilder();
        _netType = f.Row("Network type:", new ComboBox { ItemsSource = Enum.GetValues<NetType>().Select(NetTypes.DisplayName).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 24 });
        f.Row("Library:", new ComboBox { ItemsSource = new[] { "MySqlConnector (built in)" }, SelectedIndex = 0, IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 24 });
        _host = f.Text("Hostname / IP:");
        _loginPrompt = f.Check("Prompt for credentials");
        _windowsAuth = f.Check("Use Windows authentication");
        _user = f.Text("User:");
        _password = f.Text("Password:", password: true);
        _port = f.Number("Port:", 0, 65535);
        _compressed = f.Check("Compressed client/server protocol");
        _databases = new TextBox { PlaceholderText = "Separated by semicolon", MinHeight = 24, Padding = new Thickness(4, 2) }.Named("Databases");
        var pick = new Button { Content = "▼", Width = 28, Margin = new Thickness(4, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = _services.ListDatabasesAsync is not null };
        ToolTip.SetTip(pick, "Connect with these settings and pick databases");
        pick.Click += async (_, _) => await PickDatabasesAsync(pick);
        var databasesRow = new DockPanel();
        DockPanel.SetDock(pick, Dock.Right);
        databasesRow.Children.Add(pick);
        databasesRow.Children.Add(_databases);
        f.Row("Databases:", databasesRow);
        _comment = f.Row("Comment:", new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80 });
        Wire(_host, _user, _password, _databases, _comment);
        Wire(_loginPrompt, _windowsAuth, _compressed);
        Wire(_port);
        _netType.SelectionChanged += (_, _) =>
        {
            if (_loading || _model.Draft is not { } draft || _netType.SelectedIndex < 0)
            {
                return;
            }
            draft.ChangeNetType(Enum.GetValues<NetType>()[_netType.SelectedIndex]);
            LoadForm(draft);
            OnFieldChanged();
        };
        return f.Root;
    }

    private Control BuildSshTab()
    {
        var f = new FormBuilder();
        _sshActive = f.Check("Use SSH tunnel");
        var exe = f.Row("SSH executable:", new ComboBox { ItemsSource = new[] { "(built in, no executable needed)" }, SelectedIndex = 0, IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 24 });
        ToolTip.SetTip(exe, "SSH runs inside Eunomia: no plink.exe or ssh.exe, password never on a command line.");
        _sshHost = new TextBox { MinHeight = 24, Padding = new Thickness(4, 2) }.Named("SSH host");
        _sshPort = new NumericUpDown { Minimum = 1, Maximum = 65535, FormatString = "0", Width = 110, Margin = new Thickness(4, 0, 0, 0) }.Named("SSH port");
        var hostRow = new DockPanel();
        DockPanel.SetDock(_sshPort, Dock.Right);
        hostRow.Children.Add(_sshPort);
        hostRow.Children.Add(_sshHost);
        f.Row("SSH host + port:", hostRow);
        _sshUser = f.Text("Username:");
        _sshPassword = f.Text("Password:", password: true);
        _sshTimeout = f.Number("Timeout (seconds):", 1, 600);
        _sshKey = f.File("Private key file:", "Select private key file", () => this);
        _sshLocalPort = f.Number("Local port:", 0, 65535);
        ToolTip.SetTip(_sshLocalPort, "0 = any free port");
        _sshAgent = f.Check("Use SSH agent (OpenSSH or Pageant)");
        Wire(_sshHost, _sshUser, _sshPassword, _sshKey);
        Wire(_sshActive, _sshAgent);
        Wire(_sshPort, _sshTimeout, _sshLocalPort);
        return f.Root;
    }

    private Control BuildAdvancedTab()
    {
        var f = new FormBuilder();
        _startup = f.File("Startup script:", "Select startup script", () => this, new FilePickerFileType("SQL files") { Patterns = ["*.sql"] });
        _queryTimeout = f.Number("Query timeout (seconds):", 0, 86400);
        _keepAlive = f.Number("Ping every X seconds:", 0, 86400);
        _localTimeZone = f.Check("Use own client time zone");
        _fullStatus = f.Check("Get full table status");
        _cleartext = f.Check("Enable cleartext authentication");
        _forceUnicode = f.Check("Force Unicode (disable on old servers only)");
        _colorPreset = new ComboBox { ItemsSource = ColorPresets.Select(c => c.Name).ToList(), Width = 140, MinHeight = 24 }.Named("Background color");
        _colorHex = new TextBox { Width = 90, PlaceholderText = "#RRGGBB", Margin = new Thickness(4, 0, 0, 0), MinHeight = 24, Padding = new Thickness(4, 2) }.Named("Background color (#RRGGBB)");
        _colorSwatch = new Border { Width = 22, Height = 22, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(4, 0, 0, 0) };
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal };
        colorRow.Children.Add(_colorPreset);
        colorRow.Children.Add(_colorHex);
        colorRow.Children.Add(_colorSwatch);
        f.Row("Background color:", colorRow);
        _hidePattern = f.Text("Hide database pattern:", "Regular expression");
        _logPath = f.Text("Log queries to file:");
        _logDdl = f.Check("DDL queries (CREATE, ALTER, ...)");
        _logDml = f.Check("DML queries (INSERT, UPDATE, ...)");
        _readOnly = f.Check("Read-only session (block data and structure changes)", 6);
        _confirmChanges = f.Check("Ask before running statements that change data or structure");
        _metadataConnection = f.Check("Separate connection for the tree and auto completion");
        _jobs = f.Number("Connections for exports and imports:", 1, 8);
        Wire(_startup, _hidePattern, _logPath);
        Wire(_localTimeZone, _fullStatus, _cleartext, _forceUnicode, _logDdl, _logDml, _readOnly, _confirmChanges, _metadataConnection);
        Wire(_queryTimeout, _keepAlive, _jobs);
        _colorPreset.SelectionChanged += (_, _) =>
        {
            if (_loading || _colorPreset.SelectedIndex < 0 || _colorPreset.SelectedIndex >= ColorPresets.Length)
            {
                return;
            }
            SetColorText(ColorPresets[_colorPreset.SelectedIndex].Rgb);
            OnFieldChanged();
        };
        _colorHex.TextChanged += (_, _) =>
        {
            if (!_loading)
            {
                OnFieldChanged();
            }
        };
        return f.Root;
    }

    private Control BuildSslTab()
    {
        var f = new FormBuilder();
        var pem = new FilePickerFileType("Certificates") { Patterns = ["*.pem", "*.crt", "*.key"] };
        _sslActive = f.Check("Use SSL");
        _sslKey = f.File("SSL private key:", "Select SSL private key", () => this, pem);
        _sslCa = f.File("SSL CA certificate:", "Select SSL CA certificate", () => this, pem);
        _sslCert = f.File("SSL certificate:", "Select SSL certificate", () => this, pem);
        _sslCipher = f.Text("SSL cipher:", "List of permissible ciphers to use for SSL encryption");
        _sslVerify = f.Row("Certificate verification:", new ComboBox
        {
            ItemsSource = new[]
            {
                "No verification (insecure)",
                "Verify CA (insecure)",
                "Verify CA and host name identity (may fail with self-signed certs and wildcard cn)",
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 24,
        });
        Wire(_sslKey, _sslCa, _sslCert, _sslCipher);
        Wire(_sslActive);
        _sslVerify.SelectionChanged += async (_, _) =>
        {
            if (_loading)
            {
                return;
            }
            // 23.2: níveis inferiores à verificação total exigem confirmação explícita.
            if (_sslVerify.SelectedIndex is 0 or 1 && _model.Draft?.Ssl.Verification == 2)
            {
                var answer = await Eunomia.UI.Controls.Common.Dialogs.MessageDialog.ShowAsync(this, "SSL verification",
                    "Lower verification levels allow man-in-the-middle attacks. Use them only on trusted networks. Continue?",
                    Eunomia.UI.Controls.Common.Dialogs.MessageDialogButtons.YesNo);
                if (answer != Eunomia.UI.Controls.Common.Dialogs.MessageDialogResult.Yes)
                {
                    _loading = true;
                    _sslVerify.SelectedIndex = 2;
                    _loading = false;
                    return;
                }
            }
            OnFieldChanged();
        };
        return f.Root;
    }

    private Control BuildStatisticsTab()
    {
        var f = new FormBuilder();
        _statCreated = f.Row("Created:", new TextBlock());
        _statLast = f.Row("Last connect:", new TextBlock());
        _statOk = f.Row("Successful connects:", new TextBlock());
        _statFailed = f.Row("Unsuccessful connects:", new TextBlock());
        return f.Root;
    }

    /// <summary>
    /// Dropdown do campo Databases (anexo A §6): liga com os valores do formulário e mostra as bases com checkboxes; marcar
    /// ou desmarcar reescreve a lista separada por <c>;</c>. Erros de ligação aparecem como item desativado.
    /// </summary>
    private async Task PickDatabasesAsync(Control anchor)
    {
        if (_services.ListDatabasesAsync is not { } list || _model.Draft is not { } draft)
        {
            return;
        }
        var probe = draft.Clone();
        probe.Databases = string.Empty;
        var (_, credentials) = _model.ValuesForOpen();
        IReadOnlyList<string> names;
        try
        {
            names = await list(probe, credentials);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            names = [];
            new MenuFlyout { ItemsSource = new[] { new MenuItem { Header = LiteralText.Escaped(ex.Message), IsEnabled = false } } }.ShowAt(anchor);
            return;
        }
        var selected = (_databases.Text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var items = names.Select(n =>
        {
            var item = new MenuItem { Header = LiteralText.Escaped(n), ToggleType = MenuItemToggleType.CheckBox, IsChecked = selected.Contains(n, StringComparer.OrdinalIgnoreCase), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (selected.Contains(n, StringComparer.OrdinalIgnoreCase))
                {
                    selected.RemoveAll(s => string.Equals(s, n, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    selected.Add(n);
                }
                item.IsChecked = selected.Contains(n, StringComparer.OrdinalIgnoreCase);
                _databases.Text = string.Join(";", selected);
            };
            return item;
        }).ToList();
        new MenuFlyout { ItemsSource = items }.ShowAt(anchor);
    }

    /// <summary>Carrega o rascunho para os controlos.</summary>
    private void LoadForm(SessionDefinition d)
    {
        _loading = true;
        try
        {
            _netType.SelectedIndex = Array.IndexOf(Enum.GetValues<NetType>(), d.NetType);
            _host.Text = d.Host;
            _loginPrompt.IsChecked = d.LoginPrompt;
            _windowsAuth.IsChecked = d.WindowsAuth;
            _user.Text = d.User;
            _password.Text = _model.Password;
            _port.Value = d.Port;
            _compressed.IsChecked = d.Compressed;
            _databases.Text = d.Databases;
            _comment.Text = d.Comment;

            _sshActive.IsChecked = d.Ssh.Active;
            _sshHost.Text = d.Ssh.Host;
            _sshHost.PlaceholderText = d.Host;
            _sshPort.Value = d.Ssh.Port;
            _sshUser.Text = d.Ssh.User;
            _sshPassword.Text = _model.SshPassword;
            _sshTimeout.Value = d.Ssh.Timeout;
            _sshKey.Text = d.Ssh.PrivateKeyFile;
            _sshLocalPort.Value = d.Ssh.LocalPort;
            _sshAgent.IsChecked = d.Ssh.UseAgent;

            _startup.Text = d.StartupScript;
            _queryTimeout.Value = d.QueryTimeout;
            _keepAlive.Value = d.KeepAlive;
            _localTimeZone.IsChecked = d.LocalTimeZone;
            _fullStatus.IsChecked = d.FullTableStatus;
            _cleartext.IsChecked = d.CleartextAuth;
            _forceUnicode.IsChecked = d.ForceUnicode;
            var preset = Array.FindIndex(ColorPresets, c => c.Rgb == d.Color);
            _colorPreset.SelectedIndex = preset;
            SetColorText(d.Color);
            _hidePattern.Text = d.HideDatabasePattern;
            _logPath.Text = d.LogFilePath;
            _logDdl.IsChecked = d.LogDdl;
            _logDml.IsChecked = d.LogDml;
            _readOnly.IsChecked = d.ReadOnly;
            _confirmChanges.IsChecked = d.ConfirmChanges;
            _metadataConnection.IsChecked = d.MetadataConnection;
            _jobs.Value = d.JobConnections;

            _sslActive.IsChecked = d.Ssl.Active;
            _sslKey.Text = d.Ssl.PrivateKey;
            _sslCa.Text = d.Ssl.CaCertificate;
            _sslCert.Text = d.Ssl.Certificate;
            _sslCipher.Text = d.Ssl.Cipher;
            _sslVerify.SelectedIndex = Math.Clamp(d.Ssl.Verification, 0, 2);

            var now = DateTimeOffset.UtcNow;
            _statCreated.Text = d.Statistics.Created?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "unknown";
            _statLast.Text = SessionManagerModel.Ago(d.Statistics.LastConnect, now);
            ToolTip.SetTip(_statLast, d.Statistics.LastConnect?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            _statOk.Text = d.Statistics.ConnectCount.ToString(CultureInfo.InvariantCulture);
            _statFailed.Text = d.Statistics.RefusedCount.ToString(CultureInfo.InvariantCulture);
            UpdateEnabledFields(d);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Lê os controlos para o rascunho (cada alteração).</summary>
    private void ApplyForm(SessionDefinition d)
    {
        d.Host = _host.Text ?? string.Empty;
        d.LoginPrompt = _loginPrompt.IsChecked == true;
        d.WindowsAuth = _windowsAuth.IsChecked == true;
        if (d.LoginPrompt && d.WindowsAuth)
        {
            // Mutuamente exclusivos (anexo A §1.6).
            d.WindowsAuth = false;
            _loading = true;
            _windowsAuth.IsChecked = false;
            _loading = false;
        }
        d.User = _user.Text ?? string.Empty;
        _model.Password = _password.Text ?? string.Empty;
        d.Port = (int)(_port.Value ?? d.Port);
        d.Compressed = _compressed.IsChecked == true;
        d.Databases = _databases.Text ?? string.Empty;
        d.Comment = _comment.Text ?? string.Empty;

        d.Ssh.Active = _sshActive.IsChecked == true;
        d.Ssh.Host = _sshHost.Text ?? string.Empty;
        d.Ssh.Port = (int)(_sshPort.Value ?? 22);
        d.Ssh.User = _sshUser.Text ?? string.Empty;
        _model.SshPassword = _sshPassword.Text ?? string.Empty;
        d.Ssh.Timeout = (int)(_sshTimeout.Value ?? 4);
        d.Ssh.PrivateKeyFile = _sshKey.Text ?? string.Empty;
        d.Ssh.LocalPort = (int)(_sshLocalPort.Value ?? 0);
        d.Ssh.UseAgent = _sshAgent.IsChecked == true;

        d.StartupScript = _startup.Text ?? string.Empty;
        d.QueryTimeout = (int)(_queryTimeout.Value ?? 0);
        d.KeepAlive = (int)(_keepAlive.Value ?? 0);
        d.LocalTimeZone = _localTimeZone.IsChecked == true;
        d.FullTableStatus = _fullStatus.IsChecked == true;
        d.CleartextAuth = _cleartext.IsChecked == true;
        d.ForceUnicode = _forceUnicode.IsChecked == true;
        d.Color = ParseColor(_colorHex.Text);
        _colorSwatch.Background = d.Color is { } c ? new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)) : Brushes.Transparent;
        d.HideDatabasePattern = _hidePattern.Text ?? string.Empty;
        d.LogFilePath = _logPath.Text ?? string.Empty;
        d.LogDdl = _logDdl.IsChecked == true;
        d.LogDml = _logDml.IsChecked == true;
        d.ReadOnly = _readOnly.IsChecked == true;
        d.ConfirmChanges = _confirmChanges.IsChecked == true;
        d.MetadataConnection = _metadataConnection.IsChecked == true;
        d.JobConnections = (int)(_jobs.Value ?? 2);

        d.Ssl.Active = _sslActive.IsChecked == true;
        d.Ssl.PrivateKey = _sslKey.Text ?? string.Empty;
        d.Ssl.CaCertificate = _sslCa.Text ?? string.Empty;
        d.Ssl.Certificate = _sslCert.Text ?? string.Empty;
        d.Ssl.Cipher = _sslCipher.Text ?? string.Empty;
        d.Ssl.Verification = Math.Max(0, _sslVerify.SelectedIndex);
        UpdateEnabledFields(d);
    }

    /// <summary>Campos ativos conforme o tipo de rede e as opções (anexo A §1.6, coluna "Enabled when").</summary>
    private void UpdateEnabledFields(SessionDefinition d)
    {
        var mysql = NetTypes.IsMySqlFamily(d.NetType);
        var credentials = !d.LoginPrompt && !d.WindowsAuth;
        _user.IsEnabled = credentials;
        _password.IsEnabled = credentials;
        _compressed.IsEnabled = mysql;
        _windowsAuth.IsEnabled = mysql;
        _localTimeZone.IsEnabled = mysql;
        _cleartext.IsEnabled = mysql;
        _forceUnicode.IsEnabled = mysql;
        foreach (var c in new Control[] { _sshHost, _sshPort, _sshUser, _sshPassword, _sshTimeout, _sshKey, _sshLocalPort, _sshAgent })
        {
            c.IsEnabled = d.Ssh.Active;
        }
        foreach (var c in new Control[] { _sslKey, _sslCa, _sslCert, _sslCipher, _sslVerify })
        {
            c.IsEnabled = d.Ssl.Active;
        }
        _logPath.IsEnabled = d.LogDdl || d.LogDml;
    }

    private void SetColorText(uint? rgb)
    {
        var loading = _loading;
        _loading = true;
        _colorHex.Text = rgb is { } c ? "#" + c.ToString("X6", CultureInfo.InvariantCulture) : string.Empty;
        _colorSwatch.Background = rgb is { } v ? new SolidColorBrush(Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)) : Brushes.Transparent;
        _loading = loading;
    }

    private static uint? ParseColor(string? text)
    {
        var hex = (text ?? string.Empty).Trim().TrimStart('#');
        return hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private void Wire(params TextBox[] boxes)
    {
        foreach (var b in boxes)
        {
            b.TextChanged += (_, _) => OnFieldChanged();
        }
    }

    private void Wire(params CheckBox[] boxes)
    {
        foreach (var b in boxes)
        {
            b.IsCheckedChanged += (_, _) => OnFieldChanged();
        }
    }

    private void Wire(params NumericUpDown[] boxes)
    {
        foreach (var b in boxes)
        {
            b.ValueChanged += (_, _) => OnFieldChanged();
        }
    }

    private void OnFieldChanged()
    {
        if (_loading || _model.Draft is not { } draft)
        {
            return;
        }
        ApplyForm(draft);
        RefreshButtons();
        RefreshListMarkers();
    }
}
