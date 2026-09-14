namespace NffiTrackingSystem.Shared.Models;
public sealed class NffiMessage {
    public PositionalData PositionalData { get; set; } = new();
    public Identification Identification { get; set; } = new();
    public Status Status { get; set; } = new();
    public List<RoutePoint>? RoutePoints { get; set; }
    public int UpdateIntervalMs { get; set; }
}
public sealed class RoutePoint {
    public double Lat { get; set; }
    public double Lon { get; set; }
}