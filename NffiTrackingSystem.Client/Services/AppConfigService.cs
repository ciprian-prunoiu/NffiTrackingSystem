using System.IO;
using System.Text.Json;
using NffiTrackingSystem.Shared.Config;
namespace NffiTrackingSystem.Client.Services;
public sealed class AppConfigService {
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    public AppConfig Config { get; private set; } = new();
    public AppConfigService() {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NffiTrackingSystem");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "config.json");
        Load();
    }
    public void Load() {
        if (!File.Exists(_path)) return;
        try { Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path)) ?? new AppConfig(); }
        catch { Config = new AppConfig(); }
    }
    public void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(Config, Options));
    public bool HasValidGoogleMapsApiKey() {
        var k = Config.GoogleMapsApiKey;
        return !string.IsNullOrWhiteSpace(k) && k != "YOUR_GOOGLE_MAPS_API_KEY" && k.StartsWith("AIza", StringComparison.OrdinalIgnoreCase);
    }
}