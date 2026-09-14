using System.IO;
using System.Security.Cryptography.X509Certificates;
namespace NffiTrackingSystem.Shared.Security;
public static class CertificateHelper {
    public static X509Certificate2 LoadPfx(string path, string? pwd) {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        return new X509Certificate2(path, pwd, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }
    public static X509Certificate2 LoadCert(string path) {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        return new X509Certificate2(path);
    }
    public static bool ValidateAgainstRoot(X509Certificate2 cert, X509Certificate2 root) {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        return chain.Build(cert);
    }
}