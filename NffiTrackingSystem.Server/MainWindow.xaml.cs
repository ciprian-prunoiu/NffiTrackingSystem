using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using NffiTrackingSystem.Core.Server;
using NffiTrackingSystem.Shared.Models;
using NffiTrackingSystem.Shared.Security;

namespace NffiTrackingSystem.Server;

public partial class MainWindow : Window
{
    // UI log entries shown in LogListBox
    private readonly ObservableCollection<string> _logs = new();

    // TCP server (null when not started)
    private TcpServerService? _server;

    // SQLite database service (null when not started)
    private DatabaseService? _db;

    // Count of NFFI messages received in this session
    private int _messageCount;

    // Cached history for the replay feature
    private List<PositionRecord> _replayData = new();

    // Timer driving the replay play/pause
    private DispatcherTimer? _replayTimer;

    // True when the replay is currently playing
    private bool _isPlaying;

    // Set to true once the WebView2 map signals "ready"
    private bool _mapReady = false;

    // Port requested via --autostart on the command line (delayed until the map is ready)
    private int? _pendingAutoPort = null;

    public MainWindow()
    {
        InitializeComponent();

        // Bind log collection to the ListBox
        LogListBox.ItemsSource = _logs;

        // Hook lifetime events
        Loaded += MainWindow_Loaded;
        Closed += async (_, __) =>
        {
            _replayTimer?.Stop();
            if (_server is not null) await _server.StopAsync();
            _db?.Dispose();
        };
    }

    // Runs once when the window is loaded: loads the map and parses --autostart.
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Initialize the WebView2 map control
            await MapWebView.EnsureCoreWebView2Async();
            MapWebView.CoreWebView2.WebMessageReceived += OnWebViewMessageReceived;

            // Locate map.html next to the executable
            var mapPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "map.html");
            if (File.Exists(mapPath))
            {
                // Serve map.html via the virtual host https://nffi.local
                MapWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "nffi.local", Path.GetDirectoryName(mapPath)!,
                    CoreWebView2HostResourceAccessKind.Allow);
                MapWebView.CoreWebView2.Navigate("https://nffi.local/map.html?offline=1");
                Log("Loading server map...");
            }
            else Log($"Cannot find {mapPath}.");

            // Parse --autostart <port> from the command line.
            // The actual server start is deferred until the map signals "ready".
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--autostart")
                {
                    string portStr = (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        ? args[i + 1]
                        : "8888";
                    _pendingAutoPort = int.Parse(portStr);
                    Log($"Auto-start pending on port {_pendingAutoPort} (waiting for map ready)");
                    break;
                }
            }
        }
        catch (Exception ex) { Log($"Startup error: {ex.Message}"); }
    }

    // Receives messages from the map's JavaScript.
    // When we get "ready", we can start the server (either manually or via --autostart).
    private void OnWebViewMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ready")
            {
                _mapReady = true;
                Log("Map ready signal received.");

                // If --autostart was requested, trigger Start Server now
                if (_pendingAutoPort.HasValue)
                {
                    int port = _pendingAutoPort.Value;
                    _pendingAutoPort = null;
                    Dispatcher.Invoke(() =>
                    {
                        PortTextBox.Text = port.ToString();
                        StartServerButton_Click(StartServerButton, new RoutedEventArgs());
                    });
                }
            }
        }
        catch (Exception ex) { Log($"WebView msg error: {ex.Message}"); }
    }

    // Show/hide the TLS panel when the checkbox is toggled
    private void TlsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (TlsPanel is null) return;
        TlsPanel.Visibility = TlsCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // Starts the TCP server and opens the SQLite database.
    private async void StartServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortTextBox.Text, out int port))
        {
            MessageBox.Show("Invalid port.");
            return;
        }

        try
        {
            // Open (or create) the SQLite database
            _db = new DatabaseService("nffi_tracking.db");
            await _db.InitializeAsync();

            // Build TLS options if the checkbox is checked
            TlsOptions? tls = null;
            if (TlsCheckBox.IsChecked == true)
            {
                tls = new TlsOptions
                {
                    Enabled = true,
                    ServerCertificatePath = ServerPfxTextBox.Text,
                    ServerCertificatePassword = ServerPfxPasswordTextBox.Text,
                    RootCaCertificatePath = RootCaTextBox.Text,
                    RequireClientCertificate = true
                };
            }

            // Create and start the TCP server
            _server = new TcpServerService(port, tls);
            _server.Log += OnServerLog;
            _server.MessageReceived += OnMessageReceived;
            _server.Start();

            // Update UI state
            StatusText.Text = $"Running on port {port}" + (tls is null ? "" : " (TLS)");
            StatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
            StartServerButton.IsEnabled = false;
            StopServerButton.IsEnabled = true;
            PortTextBox.IsEnabled = false;

            // Load the replay unit list from the DB
            await RefreshReplayUnitsAsync();
        }
        catch (Exception ex) { MessageBox.Show($"Startup error: {ex.Message}"); }
    }

    // Stops the TCP server.
    private async void StopServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_server is not null) { await _server.StopAsync(); _server = null; }

        StatusText.Text = "Stopped";
        StatusText.Foreground = System.Windows.Media.Brushes.DarkRed;
        StartServerButton.IsEnabled = true;
        StopServerButton.IsEnabled = false;
        PortTextBox.IsEnabled = true;
    }

    // Appends a server-generated message to the log list.
    private void OnServerLog(string msg) => Dispatcher.Invoke(() =>
    {
        _logs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (_logs.Count > 500) _logs.RemoveAt(0);
    });

    // Called for every NFFI message received from a client.
    // Saves to DB and forwards to the map.
    private async void OnMessageReceived(string ep, NffiMessage msg, int seq, string rawXml)
    {
        _messageCount++;

        // Persist to SQLite
        try { if (_db is not null) await _db.InsertPositionAsync(msg, seq, rawXml); }
        catch (Exception ex) { OnServerLog($"DB error: {ex.Message}"); }

        Dispatcher.Invoke(() =>
        {
            // Log the received position
            _logs.Add($"[{DateTime.Now:HH:mm:ss}] {ep} -> {msg.Identification.UnitId} " +
                      $"({msg.PositionalData.Coordinates.Latitude:F4}, " +
                      $"{msg.PositionalData.Coordinates.Longitude:F4}) seq={seq}");
            if (_logs.Count > 500) _logs.RemoveAt(0);

            // If the message carries a full route (first message of a trip),
            // draw it on the server's map first
            if (msg.RoutePoints is { Count: > 1 })
            {
                SendToMap(new
                {
                    type = "route",
                    unitId = msg.Identification.UnitId,
                    points = msg.RoutePoints.Select(p => new { lat = p.Lat, lon = p.Lon }).ToArray()
                });
                OnServerLog($"Route received for {msg.Identification.UnitId}: {msg.RoutePoints.Count} points.");
            }

            // Then forward the current position (animated)
            SendToMap(new
            {
                type = "position",
                unitId = msg.Identification.UnitId,
                lat = msg.PositionalData.Coordinates.Latitude,
                lon = msg.PositionalData.Coordinates.Longitude,
                heading = msg.PositionalData.Heading,
                animate = true,
                durationMs = msg.UpdateIntervalMs > 0 ? msg.UpdateIntervalMs : 500
            });
        });
    }

    // Serializes a payload to JSON and posts it to the map's JavaScript context.
    private void SendToMap(object payload)
    {
        if (MapWebView?.CoreWebView2 is null) return;
        try { MapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); } catch { }
    }

    // Populates the Replay tab with the list of distinct unit IDs.
    private async Task RefreshReplayUnitsAsync()
    {
        if (_db is null) return;
        try
        {
            var units = await _db.GetUnitIdsAsync();
            ReplayUnitCombo.Items.Clear();
            ReplayUnitCombo.Items.Add("(all units)");
            foreach (var u in units) ReplayUnitCombo.Items.Add(u);
            ReplayUnitCombo.SelectedIndex = 0;

            // Display the earliest and latest timestamps available
            var range = await _db.GetHistoryRangeAsync();
            ReplayRangeText.Text = range is null
                ? "No data."
                : $"From: {range.Value.Min:yyyy-MM-dd HH:mm:ss}\n" +
                  $"To:   {range.Value.Max:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex) { Log($"Replay refresh error: {ex.Message}"); }
    }

    // Loads the selected unit's history from SQLite and prepares the replay.
    private async void LoadHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_db is null) { MessageBox.Show("Start the server first."); return; }

        try
        {
            // "(all units)" -> null filter; otherwise filter by selected unit
            string? unitId = ReplayUnitCombo.SelectedIndex > 0
                ? ReplayUnitCombo.SelectedItem?.ToString()
                : null;

            _replayData = await _db.GetHistoryAsync(unitId);
            if (_replayData.Count == 0) { MessageBox.Show("No data."); return; }

            // Clear the current map before drawing the replay
            SendToMap(new { type = "clear" });
            SendToMap(new { type = "clearHistory" });

            // Group records by unit and send each unit's history to the map
            var byUnit = _replayData.GroupBy(p => p.UnitId);
            foreach (var g in byUnit)
            {
                var points = g.Select(p => new
                {
                    t = p.ReportedAtUtc.ToString("O"),
                    lat = p.Latitude,
                    lon = p.Longitude
                }).ToArray();
                SendToMap(new { type = "setHistory", unitId = g.Key, points });
            }

            // Configure the replay slider
            ReplaySlider.Minimum = 0;
            ReplaySlider.Maximum = Math.Max(1, _replayData.Count - 1);
            ReplaySlider.Value = 0;
            PlayPauseButton.IsEnabled = true;

            // Show the first frame
            SeekToIndex(0);
            Log($"History loaded: {_replayData.Count} records, {byUnit.Count()} units.");
        }
        catch (Exception ex) { MessageBox.Show($"Load error: {ex.Message}"); }
    }

    // Called when the replay slider is moved by the user.
    private void ReplaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_replayData.Count == 0) return;
        SeekToIndex((int)e.NewValue);
    }

    // Jumps to a specific frame index in the replay.
    private void SeekToIndex(int index)
    {
        if (_replayData.Count == 0) return;
        index = Math.Max(0, Math.Min(_replayData.Count - 1, index));

        // Tell the map to show everything up to this timestamp
        var iso = _replayData[index].ReportedAtUtc.ToString("O");
        SendToMap(new { type = "seek", t = iso });

        ReplayTimeText.Text =
            $"Frame {index + 1}/{_replayData.Count}  @  " +
            $"{_replayData[index].ReportedAtUtc:yyyy-MM-dd HH:mm:ss}";
    }

    // Toggle the replay play / pause.
    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying) { StopReplay(); return; }
        if (_replayData.Count == 0) return;

        _isPlaying = true;
        PlayPauseButton.Content = "Pause";

        // Convert the speed selector into a timer interval
        double speed = SpeedCombo.SelectedIndex switch
        {
            0 => 0.5,
            1 => 1.0,
            2 => 2.0,
            3 => 4.0,
            4 => 10.0,
            _ => 1.0
        };
        int intervalMs = Math.Max(20, (int)(200 / speed));

        _replayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
        _replayTimer.Tick += (_, __) =>
        {
            if (ReplaySlider.Value >= ReplaySlider.Maximum) { StopReplay(); return; }
            ReplaySlider.Value += 1;
        };
        _replayTimer.Start();
    }

    // Stops the replay timer and resets the Play button label.
    private void StopReplay()
    {
        _isPlaying = false;
        _replayTimer?.Stop();
        _replayTimer = null;
        PlayPauseButton.Content = "Play";
    }

    // Clears the map and the internal replay history.
    private void ClearMapButton_Click(object sender, RoutedEventArgs e)
    {
        SendToMap(new { type = "clear" });
        SendToMap(new { type = "clearHistory" });
    }

    // Appends a UI-generated message to the log list.
    private void Log(string msg) => Dispatcher.Invoke(() =>
    {
        _logs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (_logs.Count > 500) _logs.RemoveAt(0);
    });
}