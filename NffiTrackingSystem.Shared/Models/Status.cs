namespace NffiTrackingSystem.Shared.Models;
public sealed class Status {
    public string OperationalStatus { get; set; } = "OPERATIONAL";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}