using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using NffiTrackingSystem.Client.Services;
using NffiTrackingSystem.Client.Views;
using NffiTrackingSystem.Core.Client;
using NffiTrackingSystem.Shared.Security;

namespace NffiTrackingSystem.Client;

public partial class MainWindow : Window
{
    // UI log entries, displayed in the LogListBox
    private readonly ObservableCollection<string> _logs = new();

    // TCP connection to the server
    private TcpClientService? _tcp;

    // Currently running vehicle simulation
    private SimulationService? _simulation;

    // App config (Google Maps API key, offline mode, TLS paths)
    private readonly AppConfigService _config = new();

    // Currently active Google Maps API key (null if using offline mode)
    private string? _activeApiKey;

    // Whether the map is running in OpenStreetMap (keyless) mode
    private bool _useOffline;

    // Set to true once JS on the map page signals "ready"
    private bool _mapReady;

    public MainWindow()
    {
        InitializeComponent();

        // Bind the log collection to the ListBox
        LogListBox.ItemsSource = _logs;

        // Hook the Loaded event to initialize the map and config
        Loaded += MainWindow_Loaded;
    }

    // Runs once when the window is loaded. Asks for an API key if needed, then loads the map.
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // If there is no valid Google Maps key and offline mode is not set, ask the user
        if (!_config.HasValidGoogleMapsApiKey() && !_config.Config.UseOfflineMap)
        {
            var dlg = new GoogleMapsApiKeyDialog(_config.Config.GoogleMapsApiKey) { Owner = this };

            if (dlg.ShowDialog() == true)
            {
                if (dlg.UseOffline)
                {
                    // User chose OpenStreetMap
                    _config.Config.UseOfflineMap = true;
                    _config.Config.GoogleMapsApiKey = null;
                    _useOffline = true;
                }
                else
                {
                    // User provided a Google Maps API key
                    _config.Config.GoogleMapsApiKey = dlg.ChosenApiKey;
                    _config.Config.UseOfflineMap = false;
                    _activeApiKey = dlg.ChosenApiKey;
                    _useOffline = false;
                }
                _config.Save();
            }
            else
            {
                // User cancelled -> fall back to offline map
                _config.Config.UseOfflineMap = true;
                _useOffline = true;
            }
        }
        else
        {
            // Reuse previously saved configuration
            _useOffline = _config.Config.UseOfflineMap;
            _activeApiKey = _config.Config.GoogleMapsApiKey;
        }

        await LoadMapAsync();
    }

    // Loads (or reloads) the map HTML in the WebView2 control.
    private async Task LoadMapAsync()
    {
        try
        {
            await MapWebView.EnsureCoreWebView2Async();

            // Make sure we only subscribe once to WebMessageReceived
            MapWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            MapWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // The map HTML lives next to the executable, under wwwroot/
            var mapPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "map.html");
            if (!File.Exists(mapPath)) { Log($"Cannot find {mapPath}."); return; }

            // Serve map.html from the virtual host https://nffi.local
            MapWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "nffi.local", Path.GetDirectoryName(mapPath)!,
                CoreWebView2HostResourceAccessKind.Allow);

            // Pass offline / apikey hints via the query string
            var query = _useOffline
                ? "?offline=1"
                : "?apikey=" + Uri.EscapeDataString(_activeApiKey ?? "");

            _mapReady = false;
            MapStatusText.Text = "Map: loading...";
            MapStatusText.Foreground = Brushes.DarkOrange;
            UpdateStartButtonState();

            // Navigate the WebView2 to the local map page
            MapWebView.CoreWebView2.Navigate("https://nffi.local/map.html" + query);
            Log(_useOffline ? "Loading OpenStreetMap (keyless)..." : "Loading Google Maps...");
        }
        catch (Exception ex) { Log($"WebView2 error: {ex.Message}"); }
    }

    // Receives messages from JavaScript running on the map page.
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            Debug.WriteLine($"[WebView2 RX] {json}");

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ready")
            {
                var mode = doc.RootElement.TryGetProperty("mode", out var m) ? m.GetString() : "?";

                Dispatcher.Invoke(() =>
                {
                    _mapReady = true;
                    MapStatusText.Text = $"Map: ready ({mode})";
                    MapStatusText.Foreground = Brushes.DarkGreen;
                    UpdateStartButtonState();
                    Log($"Map ready ({mode}).");
                });
            }
        }
        catch (Exception ex) { Log($"WebView msg error: {ex.Message}"); }
    }

    // Show / hide the TLS panel when the checkbox changes.
    private void TlsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (TlsPanel is null) return;
        TlsPanel.Visibility = TlsCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // Opens the API key dialog manually (from the "Google Maps API Key..." button).
    private async void ApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new GoogleMapsApiKeyDialog(_config.Config.GoogleMapsApiKey) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.UseOffline)
        {
            // User wants OpenStreetMap
            _config.Config.UseOfflineMap = true;
            _config.Config.GoogleMapsApiKey = null;
            _activeApiKey = null;
            _useOffline = true;
            Log("Switched to OpenStreetMap (keyless).");
        }
        else
        {
            // User provided a new Google Maps key
            _config.Config.GoogleMapsApiKey = dlg.ChosenApiKey;
            _config.Config.UseOfflineMap = false;
            _activeApiKey = dlg.ChosenApiKey;
            _useOffline = false;
            Log("Google Maps API key updated.");
        }
        _config.Save();

        // Reload the map with the new key / mode
        await LoadMapAsync();
    }

    // Connects to the TCP server (with optional TLS).
    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ServerPortTextBox.Text, out int port))
        {
            MessageBox.Show("Invalid port.");
            return;
        }

        // Build TLS options if the checkbox is checked
        TlsOptions? tls = null;
        if (TlsCheckBox.IsChecked == true)
        {
            tls = new TlsOptions
            {
                Enabled = true,
                ClientCertificatePath = ClientPfxTextBox.Text,
                ClientCertificatePassword = ClientPfxPasswordTextBox.Text,
                RootCaCertificatePath = RootCaTextBox.Text
            };
        }

        _tcp = new TcpClientService();
        _tcp.Log += Log;

        try
        {
            await _tcp.ConnectAsync(ServerIpTextBox.Text, port, tls);
            ConnectionStatusText.Text =
                $"Connected to {ServerIpTextBox.Text}:{port}" + (tls is null ? "" : " (TLS)");
            ConnectionStatusText.Foreground = Brushes.DarkGreen;
            ConnectButton.IsEnabled = false;
            DisconnectButton.IsEnabled = true;
            UpdateStartButtonState();
        }
        catch (Exception ex)
        {
            Log($"Connection error: {ex.Message}");
            _tcp = null;
        }
    }

    // Disconnects from the server and stops any running simulation.
    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        StopSimulation();

        if (_tcp is not null)
        {
            await _tcp.DisconnectAsync();
            _tcp = null;
        }

        ConnectionStatusText.Text = "Disconnected";
        ConnectionStatusText.Foreground = Brushes.DarkRed;
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        UpdateStartButtonState();
    }

    // Enables the "Start Simulation" button only when connected AND the map is ready.
    private void UpdateStartButtonState()
    {
        StartSimulationButton.IsEnabled = (_tcp?.IsConnected == true) && _mapReady;
    }

    // Fetches a route via OSRM, draws it on the map, and starts the simulation.
    private async void StartSimulationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tcp is null || !_tcp.IsConnected) { MessageBox.Show("Not connected."); return; }
        if (!_mapReady) { MessageBox.Show("Map not ready."); return; }

        // Validate all numeric parameters from the UI
        if (!double.TryParse(LatATextBox.Text, out double latA) ||
            !double.TryParse(LonATextBox.Text, out double lonA) ||
            !double.TryParse(LatBTextBox.Text, out double latB) ||
            !double.TryParse(LonBTextBox.Text, out double lonB) ||
            !int.TryParse(IntervalTextBox.Text, out int intervalMs) ||
            !int.TryParse(StepsTextBox.Text, out int durationSec) ||
            !int.TryParse(InitialDelayTextBox.Text, out int initialDelay))
        {
            MessageBox.Show("Invalid parameters.");
            return;
        }

        // Clamp to safe minimums
        if (intervalMs < 50) intervalMs = 50;
        if (durationSec < 1) durationSec = 1;

        string unitId = UnitIdTextBox.Text;

        // Remove any previous route/marker for this unit on the map
        SendToMap(new { type = "remove", unitId });

        // Create the simulation service
        _simulation = new SimulationService(_tcp)
        {
            UnitId = unitId,
            UnitName = UnitNameTextBox.Text,
            PointA = (latA, lonA),
            PointB = (latB, lonB),
            IntervalSeconds = Math.Max(1, intervalMs / 1000),
            Steps = durationSec,
            InitialDelayMs = initialDelay,
            UseOsrm = true
        };
        _simulation.Log += Log;

        StartSimulationButton.IsEnabled = false;
        Log("Fetching optimized route (OSRM)...");

        // Fetch the route from OSRM (fallback to straight line if it fails)
        var route = await _simulation.ComputeRouteAsync();
        Log($"Route ready: {route.Count} points.");

        // Send the full route to the map so it draws it immediately
        SendToMap(new
        {
            type = "route",
            unitId,
            points = route.Select(p => new { lat = p.Lat, lon = p.Lon }).ToArray()
        });

        // Place the vehicle at the start position (no animation)
        SendToMap(new
        {
            type = "position",
            unitId,
            lat = route[0].Lat,
            lon = route[0].Lon,
            heading = _simulation.Heading,
            animate = false,
            durationMs = 0
        });

        _simulation.PositionUpdated += OnPositionUpdated;
        _simulation.Finished += OnSimulationFinished;

        Log($"Simulation started: {durationSec}s trip, update every {intervalMs}ms.");

        // Run the simulation (this awaits until the trip is finished or cancelled)
        await _simulation.RunAsync(route, intervalMs, durationSec);
    }

    // Called for every new position emitted by the simulation. Forwards it to the map.
    private void OnPositionUpdated(double lat, double lon, int tick, int total, double durationMs, double heading)
    {
        Dispatcher.Invoke(() =>
        {
            SendToMap(new
            {
                type = "position",
                unitId = UnitIdTextBox.Text,
                lat,
                lon,
                heading,
                animate = true,
                durationMs
            });
        });
    }

    // Called when the simulation finishes normally.
    private void OnSimulationFinished()
    {
        Dispatcher.Invoke(() =>
        {
            Log("Simulation finished.");
            UpdateStartButtonState();
        });
    }

    // Serializes a payload to JSON and posts it to the map's JavaScript context.
    private void SendToMap(object payload)
    {
        if (MapWebView?.CoreWebView2 is null) return;
        try
        {
            var json = JsonSerializer.Serialize(payload);
            Debug.WriteLine($"[WebView2 TX] {json}");
            MapWebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex) { Log($"Send to map failed: {ex.Message}"); }
    }

    // Cancels the current simulation.
    private void StopSimulation()
    {
        _simulation?.Cancel();
        _simulation = null;
    }

    // Appends a message to the log list (thread-safe via Dispatcher).
    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            _logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (_logs.Count > 300) _logs.RemoveAt(0);
        });
    }

    // Clean up when the window is closed.
    protected override async void OnClosed(EventArgs e)
    {
        StopSimulation();
        if (_tcp is not null) await _tcp.DisconnectAsync();
        base.OnClosed(e);
    }
}