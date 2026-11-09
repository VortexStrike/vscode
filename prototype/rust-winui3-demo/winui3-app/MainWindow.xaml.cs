using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace WinUI3RustPrototype;

public sealed partial class MainWindow : Window
{
    private const string Backend = "http://127.0.0.1:3030";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { BaseAddress = new Uri(Backend) };
    private readonly Dictionary<string, TabViewItem> _openTabs = new(StringComparer.OrdinalIgnoreCase);
    private string _currentPath = "";

    public MainWindow()
    {
        InitializeComponent();
        _ = RefreshWorkspaceAsync();
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        try
        {
            using var response = await _http.PostAsJsonAsync("/workspace", new { path = folder.Path }, JsonOptions);
            response.EnsureSuccessStatusCode();
            _currentPath = "";
            await RefreshWorkspaceAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open folder: {ex.Message}");
        }
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        var separator = _currentPath.LastIndexOf('/');
        _currentPath = separator < 0 ? "" : _currentPath[..separator];
        await RefreshWorkspaceAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWorkspaceAsync();
    }

    private async Task RefreshWorkspaceAsync()
    {
        try
        {
            var url = $"/workspace?path={Uri.EscapeDataString(_currentPath)}";
            var listing = await _http.GetFromJsonAsync<WorkspaceListing>(url, JsonOptions);
            if (listing is null)
            {
                SetStatus("Backend returned an empty workspace response");
                return;
            }

            _currentPath = listing.Path;
            WorkspaceRootText.Text = listing.Root;
            ExplorerList.ItemsSource = listing.Entries;
            SetStatus($"{listing.Entries.Count} items · {DisplayPath(listing.Path)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Rust backend unavailable: {ex.Message}");
        }
    }

    private async void ExplorerList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WorkspaceEntry entry)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            _currentPath = entry.Path;
            await RefreshWorkspaceAsync();
            return;
        }

        await OpenFileAsync(entry);
    }

    private async Task OpenFileAsync(WorkspaceEntry entry)
    {
        if (_openTabs.TryGetValue(entry.Path, out var existingTab))
        {
            EditorTabs.SelectedItem = existingTab;
            return;
        }

        try
        {
            var file = await _http.GetFromJsonAsync<FileContents>(
                $"/file?path={Uri.EscapeDataString(entry.Path)}", JsonOptions);
            if (file is null)
            {
                SetStatus("Backend returned an empty file response");
                return;
            }

            var editor = new TextBox
            {
                Text = file.Content,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 14,
                Padding = new Thickness(12),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsSpellCheckEnabled = false,
            };
            var tab = new TabViewItem
            {
                Header = entry.Name,
                Content = editor,
                Tag = entry.Path,
            };
            editor.TextChanged += (_, _) => tab.Header = $"{entry.Name} *";
            _openTabs[entry.Path] = tab;
            EditorTabs.TabItems.Add(tab);
            EditorTabs.SelectedItem = tab;
            SetStatus($"Opened {entry.Path}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open file: {ex.Message}");
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (EditorTabs.SelectedItem is not TabViewItem tab
            || tab.Tag is not string path
            || tab.Content is not TextBox editor)
        {
            SetStatus("Select an open file tab to save");
            return;
        }

        try
        {
            using var response = await _http.PutAsJsonAsync(
                "/file", new WriteFileRequest(path, editor.Text), JsonOptions);
            response.EnsureSuccessStatusCode();
            tab.Header = Path.GetFileName(path);
            SetStatus($"Saved {path}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save file: {ex.Message}");
        }
    }

    private void EditorTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is TabViewItem tab)
        {
            if (tab.Tag is string path)
            {
                _openTabs.Remove(path);
            }
            sender.TabItems.Remove(tab);
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private static string DisplayPath(string path) => string.IsNullOrEmpty(path) ? "." : path;
}

public sealed class WorkspaceListing
{
    public string Root { get; set; } = "";
    public string Path { get; set; } = "";
    public List<WorkspaceEntry> Entries { get; set; } = [];
}

public sealed class WorkspaceEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public string DisplayName => IsDirectory ? $"📁 {Name}" : $"📄 {Name}";
}

public sealed class FileContents
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed record WriteFileRequest(string Path, string Content);
