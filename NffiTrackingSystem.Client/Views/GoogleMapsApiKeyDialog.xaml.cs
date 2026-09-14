using System.Windows;
namespace NffiTrackingSystem.Client.Views;
public partial class GoogleMapsApiKeyDialog : Window {
    public string? ChosenApiKey { get; private set; }
    public bool UseOffline { get; private set; }
    public GoogleMapsApiKeyDialog(string? currentKey = null) {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(currentKey) && currentKey != "YOUR_GOOGLE_MAPS_API_KEY")
            ApiKeyTextBox.Text = currentKey;
    }
    private void SaveButton_Click(object sender, RoutedEventArgs e) {
        var key = ApiKeyTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(key) || key == "YOUR_GOOGLE_MAPS_API_KEY" || !key.StartsWith("AIza", StringComparison.OrdinalIgnoreCase)) {
            MessageBox.Show("The entered key does not look valid.\n\nA Google Maps API key usually starts with 'AIza'.",
                "Invalid key", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ChosenApiKey = key;
        UseOffline = false;
        DialogResult = true;
    }
    private void OfflineButton_Click(object sender, RoutedEventArgs e) {
        ChosenApiKey = null;
        UseOffline = true;
        DialogResult = true;
    }
    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}