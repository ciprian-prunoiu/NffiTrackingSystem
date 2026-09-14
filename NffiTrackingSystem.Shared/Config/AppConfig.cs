namespace NffiTrackingSystem.Shared.Config;
public sealed class AppConfig {
    public string? GoogleMapsApiKey { get; set; }
    public bool UseOfflineMap { get; set; }
    public bool UseTls { get; set; }
    public string? ServerCertificatePath { get; set; }
    public string? ServerCertificatePassword { get; set; }
    public string? ClientCertificatePath { get; set; }
    public string? ClientCertificatePassword { get; set; }
    public string? RootCaCertificatePath { get; set; }
}