namespace NffiTrackingSystem.Shared.Models;
public sealed class PositionalData {
    public Coordinates Coordinates { get; set; } = new();
    public double Velocity { get; set; }
    public double Heading { get; set; }
}