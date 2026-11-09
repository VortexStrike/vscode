using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace WinUI3RustPrototype;

public sealed partial class MainWindow : Window
{
    private readonly HttpClient _http = new();

    public MainWindow()
    {
        this.InitializeComponent();
        _ = LoadStatusAsync();
    }

    private async Task LoadStatusAsync()
    {
        try
        {
            var response = await _http.GetAsync("http://127.0.0.1:3030/health");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("message").GetString();
            StatusText.Text = status ?? "Backend connected";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Backend unavailable: {ex.Message}";
        }
    }

    private async void PingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var response = await _http.GetAsync("http://127.0.0.1:3030/echo?message=hello%20from%20winui3");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            ResponseText.Text = json;
        }
        catch (Exception ex)
        {
            ResponseText.Text = $"Request failed: {ex.Message}";
        }
    }
}
