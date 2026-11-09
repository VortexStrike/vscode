using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.UI.Text;
using WinRT.Interop;

namespace WinUI3RustPrototype;

public sealed partial class MainWindow : Window
{
    private const string Backend = "http://127.0.0.1:3030";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex HighlightTokens = new(
        @"//.*|\b(class|struct|enum|fn|let|mut|pub|use|mod|impl|trait|async|await|return|if|else|for|while|match|const|static|fn|var|void|new|using|namespace|public|private|readonly|int|string|bool|true|false|null|return)\b|\"(?:\\.|[^\"\\])*\"|'(?:\\.|[^'\\])*'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly HttpClient _http = new() { BaseAddress = new Uri(Backend) };
    private readonly Dictionary<string, TabViewItem> _openTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeViewNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private TreeViewNode? _rootNode;
    private string _workspaceRoot = "";
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
            CloseDocumentTabs();
            await RefreshWorkspaceAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open folder: {ex.Message}");
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshWorkspaceAsync();

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
            _workspaceRoot = listing.Root;
            WorkspaceRootText.Text = listing.Root;
            BuildTree(listing);
            SetStatus($"Workspace ready · {listing.Entries.Count} items");
        }
        catch (Exception ex)
        {
            SetStatus($"Rust backend unavailable: {ex.Message}");
        }
    }

    private void BuildTree(WorkspaceListing listing)
    {
        ExplorerTree.RootNodes.Clear();
        _nodes.Clear();
        var rootEntry = new WorkspaceEntry
        {
            Name = Path.GetFileName(listing.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Path = "",
            IsDirectory = true,
        };
        _rootNode = CreateNode(rootEntry);
        _rootNode.IsExpanded = true;
        AddEntries(_rootNode, listing.Entries);
        ExplorerTree.RootNodes.Add(_rootNode);
    }

    private void AddEntries(TreeViewNode parent, IEnumerable<WorkspaceEntry> entries)
    {
        foreach (var entry in entries)
        {
            var node = CreateNode(entry);
            parent.Children.Add(node);
        }
    }

    private TreeViewNode CreateNode(WorkspaceEntry entry)
    {
        var node = new TreeViewNode
        {
            Content = entry,
            HasUnrealizedChildren = entry.IsDirectory,
        };
        _nodes[entry.Path] = node;
        return node;
    }

    private async void ExplorerTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not WorkspaceEntry entry || !entry.IsDirectory || !args.Node.HasUnrealizedChildren)
        {
            return;
        }

        try
        {
            var listing = await GetListingAsync(entry.Path);
            args.Node.Children.Clear();
            AddEntries(args.Node, listing.Entries);
            args.Node.HasUnrealizedChildren = false;
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load folder: {ex.Message}");
        }
    }

    private async void ExplorerTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node || node.Content is not WorkspaceEntry entry)
        {
            return;
        }
        if (entry.IsDirectory)
        {
            node.IsExpanded = !node.IsExpanded;
            return;
        }
        await OpenFileAsync(entry);
    }

    private void ExplorerItem_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is WorkspaceEntry entry
            && _nodes.TryGetValue(entry.Path, out var node))
        {
            ExplorerTree.SelectedNode = node;
        }
    }

    private async void EntryMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem menuItem
            || menuItem.Tag is not string command
            || menuItem.DataContext is not WorkspaceEntry entry)
        {
            return;
        }

        try
        {
            switch (command)
            {
                case "open":
                    if (entry.IsDirectory)
                    {
                        if (_nodes.TryGetValue(entry.Path, out var folderNode))
                        {
                            folderNode.IsExpanded = true;
                        }
                    }
                    else
                    {
                        await OpenFileAsync(entry);
                    }
                    break;
                case "new-file":
                    await CreateEntryAsync(entry, false);
                    break;
                case "new-folder":
                    await CreateEntryAsync(entry, true);
                    break;
                case "rename":
                    await RenameEntryAsync(entry);
                    break;
                case "delete":
                    await DeleteEntryAsync(entry);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Explorer action failed: {ex.Message}");
        }
    }

    private async Task CreateEntryAsync(WorkspaceEntry selected, bool directory)
    {
        var parent = selected.IsDirectory ? selected.Path : ParentPath(selected.Path);
        var name = await PromptNameAsync(directory ? "New Folder" : "New File", "Create");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var endpoint = directory ? "/directory" : "/file";
        using var response = await _http.PostAsJsonAsync(endpoint, new { parent, name }, JsonOptions);
        response.EnsureSuccessStatusCode();
        await ReloadParentAsync(parent);
        SetStatus($"Created {name}");
    }

    private async Task RenameEntryAsync(WorkspaceEntry entry)
    {
        if (entry.Path.Length == 0)
        {
            SetStatus("The workspace root cannot be renamed here");
            return;
        }
        var name = await PromptNameAsync("Rename", "Rename", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name)
        {
            return;
        }
        using var response = await _http.PutAsJsonAsync("/entry", new { path = entry.Path, name }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var renamed = await response.Content.ReadFromJsonAsync<WorkspaceEntry>(JsonOptions);
        if (renamed is not null)
        {
            UpdateOpenDocumentPath(entry.Path, renamed.Path, renamed.Name);
        }
        await ReloadParentAsync(ParentPath(entry.Path));
        SetStatus($"Renamed to {name}");
    }

    private async Task DeleteEntryAsync(WorkspaceEntry entry)
    {
        if (entry.Path.Length == 0)
        {
            SetStatus("The workspace root cannot be deleted");
            return;
        }
        var confirm = new ContentDialog
        {
            Title = $"Delete {entry.Name}?",
            Content = entry.IsDirectory ? "Only empty folders can be deleted." : "This file will be permanently deleted.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        using var response = await _http.DeleteAsync($"/entry?path={Uri.EscapeDataString(entry.Path)}");
        response.EnsureSuccessStatusCode();
        CloseDocumentTab(entry.Path);
        await ReloadParentAsync(ParentPath(entry.Path));
        SetStatus($"Deleted {entry.Name}");
    }

    private async Task<string?> PromptNameAsync(string title, string action, string initial = "")
    {
        var input = new TextBox { Text = initial, MinWidth = 280 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    private async Task ReloadParentAsync(string parentPath)
    {
        if (!_nodes.TryGetValue(parentPath, out var parentNode))
        {
            await RefreshWorkspaceAsync();
            return;
        }
        var listing = await GetListingAsync(parentPath);
        parentNode.Children.Clear();
        AddEntries(parentNode, listing.Entries);
        parentNode.HasUnrealizedChildren = false;
        parentNode.IsExpanded = true;
    }

    private async Task<WorkspaceListing> GetListingAsync(string path)
    {
        var url = $"/workspace?path={Uri.EscapeDataString(path)}";
        return await _http.GetFromJsonAsync<WorkspaceListing>(url, JsonOptions)
            ?? throw new InvalidOperationException("Backend returned an empty workspace response");
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

            var editor = new RichEditBox
            {
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
            editor.Document.SetText(TextSetOptions.None, file.Content);
            var tab = new TabViewItem { Header = entry.Name, Content = editor, Tag = entry.Path };
            editor.TextChanged += (_, _) =>
            {
                tab.Header = $"{entry.Name} *";
                ApplySyntaxHighlighting(editor);
            };
            _openTabs[entry.Path] = tab;
            EditorTabs.TabItems.Add(tab);
            EditorTabs.SelectedItem = tab;
            ApplySyntaxHighlighting(editor);
            SetStatus($"Opened {entry.Path}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open file: {ex.Message}");
        }
    }

    private static void ApplySyntaxHighlighting(RichEditBox editor)
    {
        editor.Document.GetText(TextGetOptions.None, out var text);
        editor.Document.GetRange(0, text.Length).CharacterFormat.ForegroundColor = Colors.White;
        foreach (Match match in HighlightTokens.Matches(text))
        {
            var range = editor.Document.GetRange(match.Index, match.Index + match.Length);
            range.CharacterFormat.ForegroundColor = match.Value.StartsWith("//", StringComparison.Ordinal)
                ? Colors.LightGreen
                : match.Value.StartsWith('"') || match.Value.StartsWith('\'')
                    ? Colors.LightSalmon
                    : Colors.DeepSkyBlue;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (EditorTabs.SelectedItem is not TabViewItem tab
            || tab.Tag is not string path
            || tab.Content is not RichEditBox editor)
        {
            SetStatus("Select an open file tab to save");
            return;
        }
        editor.Document.GetText(TextGetOptions.None, out var content);
        try
        {
            using var response = await _http.PutAsJsonAsync("/file", new WriteFileRequest(path, content), JsonOptions);
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
        if (args.Item is TabViewItem tab && tab.Tag is string path)
        {
            CloseDocumentTab(path);
        }
    }

    private void CloseDocumentTabs()
    {
        foreach (var path in _openTabs.Keys.ToArray())
        {
            CloseDocumentTab(path);
        }
    }

    private void CloseDocumentTab(string path)
    {
        if (_openTabs.Remove(path, out var tab))
        {
            EditorTabs.TabItems.Remove(tab);
        }
    }

    private void UpdateOpenDocumentPath(string oldPath, string newPath, string name)
    {
        if (!_openTabs.Remove(oldPath, out var tab))
        {
            return;
        }
        tab.Tag = newPath;
        tab.Header = name;
        _openTabs[newPath] = tab;
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private static string ParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? "" : path[..separator];
    }
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
