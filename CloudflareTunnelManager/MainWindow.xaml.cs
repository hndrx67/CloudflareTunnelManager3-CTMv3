using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CloudflareTunnelManager.Models;
using CloudflareTunnelManager.Services;

namespace CloudflareTunnelManager;

public partial class MainWindow : Window
{
    private readonly CloudflaredManager _manager = new();
    private readonly CloudflareApiClient _api = new();
    private readonly MultiTunnelRunner _runner = new();
    private readonly ObservableCollection<CloudflareTunnel> _tunnels = new();
    private readonly ObservableCollection<TunnelRoute> _routes = new();
    private IReadOnlyList<CloudflareZone> _zones = Array.Empty<CloudflareZone>();
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        // ensure the nav highlights the dashboard on startup
        UpdateNavSelection(DashboardNav);
        Loaded += async (_, _) => await RefreshStatusAsync();
        _manager.MessageLogged += (_, message) => Dispatcher.Invoke(() => AppendLog(message));
        _runner.MessageLogged += (_, message) => Dispatcher.Invoke(() => AppendLog(message));
        TunnelList.ItemsSource = _tunnels;
        RoutesGrid.ItemsSource = _routes;
        TunnelTypeColumn.ItemsSource = new[] { "http", "https", "tcp", "ssh", "rdp", "smb" };
        Closed += (_, _) => { ApiTokenBox.Clear(); _runner.Dispose(); _api.Dispose(); };
    }

    private void ShowPage(UIElement page, string title, string subtitle)
    {
        DashboardPage.Visibility = Visibility.Collapsed;
        TunnelsPage.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Collapsed;
        LogsPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }
    private void DashboardNav_Click(object sender, RoutedEventArgs e)
    {
        UpdateNavSelection(DashboardNav);
        ShowPage(DashboardPage, "Tunnel dashboard", "Monitor and control the Cloudflare connector on this computer.");
    }

    private void TunnelsNav_Click(object sender, RoutedEventArgs e)
    {
        UpdateNavSelection(TunnelsNav);
        ShowPage(TunnelsPage, "Tunnels", "Manage multiple remotely configured tunnels and their web application routes.");
    }

    private void SetupNav_Click(object sender, RoutedEventArgs e)
    {
        UpdateNavSelection(SetupNav);
        ShowPage(SetupPage, "Set up Cloudflare Tunnel", "Connect this computer securely without opening inbound firewall ports.");
    }

    private void LogsNav_Click(object sender, RoutedEventArgs e)
    {
        UpdateNavSelection(LogsNav);
        ShowPage(LogsPage, "Activity logs", "Troubleshooting information with tunnel tokens automatically redacted.");
    }

    private void UpdateNavSelection(System.Windows.Controls.Button active)
    {
        var navs = new[] { DashboardNav, TunnelsNav, SetupNav, LogsNav };
        foreach (var btn in navs)
        {
            if (btn == active)
            {
                btn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));
                btn.Foreground = Brushes.White;
                var text = btn.Content?.ToString() ?? string.Empty;
                if (!text.StartsWith("●")) btn.Content = "●   " + text.Trim();
            }
            else
            {
                btn.Background = Brushes.Transparent;
                btn.Foreground = (Brush)FindResource("MutedBrush");
                btn.Content = btn.Content?.ToString().Replace("●   ", string.Empty).Trim();
            }
        }
    }

    private async void UninstallService_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("This will stop and remove the cloudflared Windows service and delete the installed files. Continue?", "Confirm uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        SetBusy(true, "Uninstalling cloudflared…");
        try
        {
            await _manager.UninstallAsync();
            MessageBox.Show("Cloudflared was removed from this computer.", "Uninstall complete", MessageBoxButton.OK, MessageBoxImage.Information);
            AppendLog("User removed cloudflared installation.");
        }
        catch (Exception ex)
        {
            AppendLog($"Uninstall failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Uninstall failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); await RefreshStatusAsync(); }
    }

    private async void SyncTunnels_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AccountIdBox.Text) || string.IsNullOrWhiteSpace(ApiTokenBox.Password))
        {
            MessageBox.Show("Enter both your Cloudflare Account ID and a scoped API token.", "Credentials required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetBusy(true, "Loading tunnels from Cloudflare…");
        try
        {
            _api.Configure(AccountIdBox.Text, ApiTokenBox.Password);
            var tunnels = await _api.GetTunnelsAsync();
            _zones = await _api.GetZonesAsync();
            _tunnels.Clear();
            foreach (var tunnel in tunnels) _tunnels.Add(tunnel);
            TunnelCountText.Text = _tunnels.Count.ToString();
            AppendLog($"Synchronized {_tunnels.Count} tunnel(s) and {_zones.Count} DNS zone(s) from Cloudflare.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Cloudflare connection failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); }
    }

    private async void TunnelList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TunnelList.SelectedItem is not CloudflareTunnel tunnel) return;
        SelectedTunnelName.Text = tunnel.Name;
        SelectedTunnelDetails.Text = $"{tunnel.StatusLabel}  •  {tunnel.Id}";
        _routes.Clear();
        var editable = string.Equals(tunnel.ConfigSource, "cloudflare", StringComparison.OrdinalIgnoreCase);
        SaveRoutesButton.IsEnabled = editable;
        BindDnsButton.IsEnabled = false;
        ConnectTunnelButton.IsEnabled = true;
        DisconnectTunnelButton.IsEnabled = _runner.IsRunning(tunnel.Id);
        if (!editable)
        {
            MessageBox.Show("This is a locally managed tunnel. Its ingress rules are stored in a local YAML file, so they cannot be edited through Cloudflare's remote API.", "Local configuration", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetBusy(true, $"Loading routes for {tunnel.Name}…");
        try
        {
            var routes = await _api.GetRoutesAsync(tunnel.Id);
            foreach (var route in routes) _routes.Add(route);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not load routes", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); }
    }

    private void AddRoute_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is not CloudflareTunnel) return;
        var route = new TunnelRoute();
        _routes.Add(route);
        RoutesGrid.SelectedItem = route;
        RoutesGrid.ScrollIntoView(route);
    }

    private void RoutesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        BindDnsButton.IsEnabled = RoutesGrid.SelectedItem is TunnelRoute &&
                                  TunnelList.SelectedItem is CloudflareTunnel tunnel &&
                                  string.Equals(tunnel.ConfigSource, "cloudflare", StringComparison.OrdinalIgnoreCase);
    }

    private async void CreateTunnel_Click(object sender, RoutedEventArgs e)
    {
        var name = NewTunnelNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Enter a descriptive tunnel name first.", "Tunnel name required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_api.IsConfigured)
        {
            MessageBox.Show("Connect and sync your Cloudflare account first.", "Cloudflare connection required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetBusy(true, $"Creating tunnel {name}…");
        try
        {
            var tunnel = await _api.CreateTunnelAsync(name);
            _tunnels.Add(tunnel);
            TunnelCountText.Text = _tunnels.Count.ToString();
            NewTunnelNameBox.Clear();
            TunnelList.SelectedItem = tunnel;
            AppendLog($"Created remotely managed tunnel {tunnel.Name} ({tunnel.Id}).");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not create tunnel", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); }
    }

    private async void BindDns_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is not CloudflareTunnel tunnel || RoutesGrid.SelectedItem is not TunnelRoute route) return;
        RoutesGrid.CommitEdit();
        if (string.IsNullOrWhiteSpace(route.Hostname) || Uri.CheckHostName(route.Hostname.Trim()) == UriHostNameType.Unknown)
        {
            MessageBox.Show("Select a route with a complete public hostname such as app.example.com.", "Invalid hostname", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Uri.TryCreate(route.Service, UriKind.Absolute, out _))
        {
            MessageBox.Show("Enter a valid local address and port for the selected tunnel type.", "Invalid local service", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var target = $"{tunnel.Id}.cfargotunnel.com";
        var confirmation = MessageBox.Show(
            $"Create or update this proxied DNS record?\n\n{route.Hostname}\n→ {target}\n\nThe current tunnel route configuration will also be saved.",
            "Bind hostname to tunnel", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;

        SetBusy(true, $"Binding {route.Hostname} to {tunnel.Name}…");
        try
        {
            await _api.SaveRoutesAsync(tunnel.Id, _routes);
            var result = await _api.BindDnsAsync(tunnel.Id, route.Hostname, _zones);
            AppendLog($"[{tunnel.Name}] {result}");
            MessageBox.Show(result, "DNS binding complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "DNS binding failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); }
    }

    private void RemoveRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RoutesGrid.SelectedItem is TunnelRoute route) _routes.Remove(route);
    }

    private async void SaveRoutes_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is not CloudflareTunnel tunnel) return;
        RoutesGrid.CommitEdit();
        var invalid = _routes.FirstOrDefault(route => string.IsNullOrWhiteSpace(route.Hostname) || !Uri.TryCreate(route.Service, UriKind.Absolute, out _));
        if (invalid is not null)
        {
            MessageBox.Show("Every route needs a hostname and a complete service URL such as http://localhost:3000.", "Invalid route", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SetBusy(true, $"Saving {tunnel.Name} configuration…");
        try
        {
            await _api.SaveRoutesAsync(tunnel.Id, _routes);
            AppendLog($"[{tunnel.Name}] Saved {_routes.Count} route(s).");
            MessageBox.Show("The tunnel configuration was saved. Make sure each public hostname has a proxied DNS record pointing to this tunnel.", "Configuration saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not save configuration", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); }
    }

    private async void ConnectSelectedTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is not CloudflareTunnel tunnel) return;
        SetBusy(true, $"Starting {tunnel.Name} connector…");
        try
        {
            var token = await _api.GetConnectorTokenAsync(tunnel.Id);
            await _runner.StartAsync(tunnel.Id, tunnel.Name, token, _manager.InstalledExecutablePath);
            token = string.Empty;
            ConnectTunnelButton.IsEnabled = false;
            DisconnectTunnelButton.IsEnabled = true;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Connector could not start", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); }
    }

    private void DisconnectSelectedTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is not CloudflareTunnel tunnel) return;
        _runner.Stop(tunnel.Id);
        ConnectTunnelButton.IsEnabled = true;
        DisconnectTunnelButton.IsEnabled = false;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async Task RefreshStatusAsync()
    {
        if (_busy) return;
        try
        {
            var state = await _manager.GetStateAsync();
            ServiceStatusText.Text = state.DisplayStatus;
            StartupTypeText.Text = state.StartupType;
            VersionText.Text = state.Version;
            SidebarVersion.Text = state.Version == "—" ? "Not installed" : state.Version;
            StatusDot.Fill = state.IsRunning ? new SolidColorBrush(Color.FromRgb(50, 210, 145)) :
                state.IsInstalled ? new SolidColorBrush(Color.FromRgb(255, 179, 71)) : new SolidColorBrush(Color.FromRgb(119, 129, 149));
            EmptyTitle.Text = state.IsRunning ? "Connector is running" : "Ready to configure your connector";
            EmptyMessage.Text = state.IsRunning
                ? "Cloudflared is running as a Windows service. Confirm the tunnel is Healthy in the Cloudflare dashboard."
                : "Install a remotely managed tunnel using the secure token from your Cloudflare dashboard.";
        }
        catch (Exception ex) { AppendLog($"Status check failed: {ex.Message}"); }
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var valid = TokenParser.TryExtract(TokenBox.Password, out _);
        InstallButton.IsEnabled = valid && !_busy;
        TokenHint.Text = valid ? "Token detected — ready to install" : "Paste a raw token or the complete install command";
        TokenHint.Foreground = valid ? new SolidColorBrush(Color.FromRgb(50, 210, 145)) : (Brush)FindResource("MutedBrush");
    }

    private async void InstallTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (!TokenParser.TryExtract(TokenBox.Password, out var token))
        {
            MessageBox.Show("The pasted value does not contain a valid tunnel token.", "Invalid token", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "Downloading the official Cloudflare connector…");
        try
        {
            await _manager.InstallAsync(token, progress => Dispatcher.Invoke(() => ProgressText.Text = progress));
            TokenBox.Clear();
            MessageBox.Show("Cloudflare Tunnel was installed and the Windows service started successfully.", "Installation complete", MessageBoxButton.OK, MessageBoxImage.Information);
            ShowPage(DashboardPage, "Tunnel dashboard", "Monitor and control the Cloudflare connector on this computer.");
        }
        catch (Exception ex)
        {
            AppendLog($"Installation failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Installation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { token = string.Empty; SetBusy(false); await RefreshStatusAsync(); }
    }

    private async void StartService_Click(object sender, RoutedEventArgs e) => await RunServiceAction("start");
    private async void StopService_Click(object sender, RoutedEventArgs e) => await RunServiceAction("stop");
    private async void RestartService_Click(object sender, RoutedEventArgs e) => await RunServiceAction("restart");

    private async Task RunServiceAction(string action)
    {
        SetBusy(true, $"Requesting service {action}…");
        try { await _manager.ControlServiceAsync(action); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Service operation failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); await RefreshStatusAsync(); }
    }

    private void OpenDashboard_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://one.dash.cloudflare.com/") { UseShellExecute = true });

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(LogTextBox.Text)) Clipboard.SetText(LogTextBox.Text);
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e) => LogTextBox.Clear();

    private void SetBusy(bool value, string message = "Working…")
    {
        _busy = value;
        ProgressPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = message;
        InstallButton.IsEnabled = !value && TokenParser.TryExtract(TokenBox.Password, out _);
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {TokenParser.Redact(message)}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }
}
