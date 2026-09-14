namespace NffiTrackingSystem.Shared.Security;
public sealed class TlsOptions {
    public bool Enabled { get; set; }
    public string? ServerCertificatePath { get; set; }
    public string? ServerCertificatePassword { get; set; }
    public string? ClientCertificatePath { get; set; }
    public string? ClientCertificatePassword { get; set; }
    public string? RootCaCertificatePath { get; set; }
    public bool RequireClientCertificate { get; set; } = true;
}