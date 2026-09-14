using System.Globalization;
using System.Text.Json;
using NffiTrackingSystem.Shared.Models;

namespace NffiTrackingSystem.Core.Client;

// Simulates a moving vehicle from point A to point B.
// Fetches a route (OSRM or straight line), then emits NFFI messages
// at fixed intervals until the vehicle reaches destination B.
public sealed class SimulationService
{
    // TCP client used to send NFFI messages to the server
    private readonly TcpClientService _tcp;

    // Cancellation token source for the running simulation
    private CancellationTokenSource? _cts;

    // Vehicle identification
    public string UnitId { get; set; } = "MASINA_01";
    public string UnitName { get; set; } = "Vehicle";

    // Route endpoints (latitude, longitude)
    public (double Lat, double Lon) PointA { get; set; }
    public (double Lat, double Lon) PointB { get; set; }

    // Timing configuration
    public int IntervalSeconds { get; set; } = 1;
    public int Steps { get; set; } = 30;
    public int InitialDelayMs { get; set; } = 500;

    // If true, fetch real road-following routes from OSRM; otherwise use straight line
    public bool UseOsrm { get; set; } = true;

    // Current heading (degrees, 0 = north). Updated while moving.
    public double Heading { get; private set; }

    // Events raised to the UI
    public event Action<string>? Log;
    public event Action<double, double, int, int, double, double>? PositionUpdated;
    public event Action? Finished;

    public SimulationService(TcpClientService tcp) => _tcp = tcp;

    // Computes the route between PointA and PointB.
    // Tries OSRM first (if enabled), falls back to a straight line.
    public async Task<List<(double Lat, double Lon)>> ComputeRouteAsync()
    {
        // Try OSRM first
        if (UseOsrm)
        {
            try
            {
                var r = await FetchOsrmRouteAsync(PointA, PointB);
                if (r.Count >= 2)
                {
                    // Set initial heading from first two points of the route
                    Heading = ComputeHeading(r[0], r[1]);
                    Log?.Invoke($"OSRM route: {r.Count} points.");
                    return r;
                }
            }
            catch (Exception ex)
            {
                // Fall through to straight line
                Log?.Invoke($"OSRM failed: {ex.Message}. Using straight line.");
            }
        }

        // Fallback: interpolate a straight line from A to B in Steps segments
        Heading = ComputeHeading(PointA, PointB);
        var pts = new List<(double, double)>();
        for (int i = 0; i <= Steps; i++)
        {
            double t = Steps == 0 ? 0 : (double)i / Steps;
            pts.Add((
                PointA.Lat + (PointB.Lat - PointA.Lat) * t,
                PointA.Lon + (PointB.Lon - PointA.Lon) * t));
        }
        Log?.Invoke($"Straight line: {pts.Count} points.");
        return pts;
    }

    // Calls the public OSRM server to get a real road-following route.
    private static async Task<List<(double Lat, double Lon)>> FetchOsrmRouteAsync(
        (double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        // OSRM expects "lon,lat;lon,lat" with invariant decimal separator
        var ci = CultureInfo.InvariantCulture;
        string url = "https://router.project-osrm.org/route/v1/driving/" +
                     $"{a.Lon.ToString(ci)},{a.Lat.ToString(ci)};" +
                     $"{b.Lon.ToString(ci)},{b.Lat.ToString(ci)}" +
                     "?overview=full&geometries=geojson";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("User-Agent", "NffiTrackingSystem/1.0");

        // Fetch and parse the JSON response
        var json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Extract the first route's geometry coordinates
        if (!root.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0)
            throw new Exception("No routes.");

        var coords = routes[0].GetProperty("geometry").GetProperty("coordinates");

        // OSRM returns coordinates as [lon, lat] pairs; we flip to (lat, lon)
        var result = new List<(double, double)>();
        foreach (var c in coords.EnumerateArray())
            result.Add((c[1].GetDouble(), c[0].GetDouble()));

        return result;
    }

    // Runs the simulation: emits NFFI messages along the route at fixed intervals.
    public async Task RunAsync(IReadOnlyList<(double Lat, double Lon)> route, int intervalMs, int durationSec)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Wait a moment before starting so the map has time to render the route
        try { await Task.Delay(InitialDelayMs, token); }
        catch (OperationCanceledException) { Finished?.Invoke(); return; }

        // Route must have at least two points to move along
        if (route.Count < 2) { Finished?.Invoke(); return; }

        // Compute how many ticks we will emit over the total duration
        int totalTicks = Math.Max(1, durationSec * 1000 / intervalMs);
        int lastIdx = route.Count - 1;

        // Main loop: one iteration = one NFFI message
        for (int tick = 1; tick <= totalTicks && !token.IsCancellationRequested; tick++)
        {
            // Fraction of the trip completed (0..1)
            double frac = (double)tick / totalTicks;

            // Position along the route array (interpolated between two points)
            double pos = frac * lastIdx;
            int idx = (int)Math.Floor(pos);
            int nxt = Math.Min(idx + 1, lastIdx);
            double local = pos - idx;

            // Linearly interpolate between the two nearest route points
            double lat = route[idx].Lat + (route[nxt].Lat - route[idx].Lat) * local;
            double lon = route[idx].Lon + (route[nxt].Lon - route[idx].Lon) * local;

            // Compute heading from current segment
            Heading = ComputeHeading(route[idx], route[nxt]);

            // Build the NFFI message
            var msg = new NffiMessage
            {
                Identification = new Identification { UnitId = UnitId, Name = UnitName },
                PositionalData = new PositionalData
                {
                    Coordinates = new Coordinates { Latitude = lat, Longitude = lon, Altitude = 85 },
                    Velocity = 55.5,
                    Heading = Heading
                },
                Status = new Status
                {
                    OperationalStatus = "OPERATIONAL",
                    TimestampUtc = DateTime.UtcNow
                },
                UpdateIntervalMs = intervalMs
            };

            // On the very first message, include the full planned route
            // so the server can draw it immediately
            if (tick == 1)
            {
                msg.RoutePoints = route
                    .Select(p => new RoutePoint { Lat = p.Lat, Lon = p.Lon })
                    .ToList();
            }

            // Send the message to the server
            try { await _tcp.SendNffiAsync(msg); }
            catch (Exception ex)
            {
                Log?.Invoke($"Send error: {ex.Message}");
                break;
            }

            // Notify the UI / local map
            PositionUpdated?.Invoke(lat, lon, tick, totalTicks, intervalMs, Heading);

            // Wait for the next tick (unless we are at the last one)
            if (tick < totalTicks)
            {
                try { await Task.Delay(intervalMs, token); }
                catch (OperationCanceledException) { break; }
            }
        }

        Finished?.Invoke();
    }

    // Requests cancellation of the running simulation.
    public void Cancel() => _cts?.Cancel();

    // Computes the initial bearing (heading) from point a to point b, in degrees.
    // 0 = north, 90 = east, 180 = south, 270 = west.
    private static double ComputeHeading((double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        double dLon = (b.Lon - a.Lon) * Math.PI / 180.0;
        double lat1 = a.Lat * Math.PI / 180.0;
        double lat2 = b.Lat * Math.PI / 180.0;
        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) -
                   Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        double brng = Math.Atan2(y, x) * 180.0 / Math.PI;
        return (brng + 360.0) % 360.0; // normalize to [0, 360)
    }
}