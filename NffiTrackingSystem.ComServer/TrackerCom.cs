using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NffiTrackingSystem.Core.Client;
using NffiTrackingSystem.Core.Server;
using NffiTrackingSystem.Shared.Models;

namespace NffiTrackingSystem.ComServer;
[ComVisible(true)]
[Guid("A1111111-2222-3333-4444-555555555555")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface ITrackerCom {
    string GetVersion();
    void StartServer(int port, string dbPath);
    void StopServer();
    void AddVehicle(string host, int port, string unitId,
                    double latA, double lonA, double latB, double lonB,
                    int intervalMs, int durationSec);
    string[] GetActiveVehicles();
    string GetLastPosition(string unitId);
    void StopVehicle(string unitId);
    void StopAll();
}
[ComVisible(true)]
[Guid("B2222222-3333-4444-5555-666666666666")]
[ProgId("NffiTracking.Tracker")]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(ITrackerCom))]
public sealed class TrackerCom : ITrackerCom {
    private TcpServerService? _server;
    private DatabaseService? _db;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _vehicles = new();
    public string GetVersion() => "NFFI Tracker COM v2 (Core-backed)";
    public void StartServer(int port, string dbPath) {
        if (_server != null) throw new InvalidOperationException("Server already running.");
        _db = new DatabaseService(dbPath);
        _db.InitializeAsync().GetAwaiter().GetResult();
        _server = new TcpServerService(port, tls: null);
        _server.MessageReceived += OnMessageReceived;
        _server.Start();
    }
    public void StopServer() {
        _server?.StopAsync().GetAwaiter().GetResult();
        _server = null;
        _db?.Dispose();
        _db = null;
    }
    private void OnMessageReceived(string ep, NffiMessage msg, int seq, string rawXml) {
        try { _db?.InsertPositionAsync(msg, seq, rawXml).GetAwaiter().GetResult(); } catch { }
    }
    public void AddVehicle(string host, int port, string unitId,
                           double latA, double lonA, double latB, double lonB,
                           int intervalMs, int durationSec) {
        if (intervalMs < 50) intervalMs = 50;
        if (durationSec < 1) durationSec = 1;
        if (_vehicles.TryRemove(unitId, out var old)) { try { old.Cancel(); } catch { } }
        var cts = new CancellationTokenSource();
        _vehicles[unitId] = cts;
        var token = cts.Token;
        _ = Task.Run(async () => {
            try {
                var tcp = new TcpClientService();
                await tcp.ConnectAsync(host, port, tls: null);
                var sim = new SimulationService(tcp) {
                    UnitId = unitId,
                    UnitName = unitId,
                    PointA = (latA, lonA),
                    PointB = (latB, lonB),
                    IntervalSeconds = Math.Max(1, intervalMs / 1000),
                    Steps = durationSec,
                    InitialDelayMs = 100,
                    UseOsrm = true
                };
                var route = await sim.ComputeRouteAsync();
                await sim.RunAsync(route, intervalMs, durationSec);
            } catch { }
            finally {
                if (_vehicles.TryGetValue(unitId, out var cur) && cur == cts) _vehicles.TryRemove(unitId, out _);
            }
        }, CancellationToken.None);
    }
    public string[] GetActiveVehicles() => _vehicles.Keys.ToArray();
    public string GetLastPosition(string unitId) {
        if (_db is null) return "(server not running)";
        try {
            var records = _db.GetHistoryAsync(unitId).GetAwaiter().GetResult();
            if (records.Count == 0) return "(no data)";
            var last = records[^1];
            return $"{last.Latitude:F6},{last.Longitude:F6} @ {last.ReportedAtUtc:O}";
        } catch (Exception ex) { return $"error: {ex.Message}"; }
    }
    public void StopVehicle(string unitId) {
        if (_vehicles.TryRemove(unitId, out var cts)) { try { cts.Cancel(); } catch { } }
    }
    public void StopAll() {
        foreach (var kv in _vehicles) { try { kv.Value.Cancel(); } catch { } }
        _vehicles.Clear();
        StopServer();
    }
}