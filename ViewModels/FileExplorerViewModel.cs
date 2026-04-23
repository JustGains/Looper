using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using JustCode.Services;

namespace JustCode.ViewModels;

/// Tree-node for the file-explorer panel. Lazy-loads children on expansion so
/// opening a project with thousands of files doesn't block the UI thread. A
/// dummy placeholder child is inserted for unexpanded directories so the
/// TreeView renders the expander arrow without us having to eagerly walk.
public sealed class FileNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private static readonly FileNode DummyChild = new() { Name = "…" };

    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }

    public ObservableCollection<FileNode> Children { get; } = new();

    private bool _loaded;
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            // Folder icon has open/closed variants — clear the cache + re-trigger
            // async load so the TreeView swaps to the matching drawing. Only
            // costs one extra background task per expand/collapse.
            if (IsDirectory)
            {
                _icon = null;
                _iconLoadStarted = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
            if (value && !_loaded && IsDirectory) LoadChildren();
        }
    }

    public string Glyph => IsDirectory ? "📁" : "📄";

    // Icon loading is deferred to a background task so parsing material-theme
    // SVGs doesn't freeze the UI thread when WPF realizes a batch of
    // TreeViewItems. First binding sees `null` → a task fires on the thread
    // pool → DrawingImage is produced, frozen, and surfaced via PropertyChanged.
    // Subsequent accesses return the cached drawing synchronously.
    private ImageSource? _icon;
    private bool _iconLoadStarted;
    public ImageSource? Icon
    {
        get
        {
            if (_icon != null) return _icon;
            if (!_iconLoadStarted)
            {
                _iconLoadStarted = true;
                LoadIconAsync();
            }
            return null;
        }
    }

    private async void LoadIconAsync()
    {
        var name = Name;
        var isDir = IsDirectory;
        var isExpanded = IsExpanded;
        var img = await Task.Run(() => isDir
            ? (ImageSource?)FileIconService.GetFolderIcon(name, open: isExpanded)
            : FileIconService.GetFileIcon(name));
        if (img == null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        await dispatcher.InvokeAsync(() =>
        {
            _icon = img;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        });
    }

    /// Build the initial stub. For directories we add a placeholder child so
    /// the TreeView shows the expander; real contents load when expanded.
    public static FileNode CreateRoot(string path)
    {
        var node = new FileNode
        {
            Name = Path.GetFileName(path.TrimEnd('\\', '/')) ?? path,
            FullPath = path,
            IsDirectory = true,
        };
        node.Children.Add(DummyChild);
        return node;
    }

    private void LoadChildren()
    {
        _loaded = true;
        Children.Clear();
        try
        {
            var dirs = Directory.EnumerateDirectories(FullPath)
                .Where(d => !IsSkipped(Path.GetFileName(d) ?? ""))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                var child = new FileNode
                {
                    Name = Path.GetFileName(d) ?? d,
                    FullPath = d,
                    IsDirectory = true,
                };
                child.Children.Add(DummyChild);
                Children.Add(child);
            }

            var files = Directory.EnumerateFiles(FullPath)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                Children.Add(new FileNode
                {
                    Name = Path.GetFileName(f) ?? f,
                    FullPath = f,
                    IsDirectory = false,
                });
            }
        }
        catch
        {
            // Permission denied / race on deletion — leave the empty list.
        }
    }

    /// Directory-name blocklist. Mirrors the package-json discovery skip set:
    /// skip heavy build output and dotfile trees that nobody wants to browse.
    private static bool IsSkipped(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (name.StartsWith('.')) return true; // .git, .vscode, .idea, .next, .cache, .venv…
        return name is "node_modules" or "dist" or "build" or "out" or "target"
            or "bin" or "obj" or "coverage" or "__pycache__" or "venv";
    }
}

public sealed class FileExplorerViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string WorkingDirectory { get; }
    public ObservableCollection<FileNode> Roots { get; } = new();

    private bool _isActive;
    private bool _loadedOnce;
    /// Only walks the tree the first time the Files tab is activated. Flips
    /// back to false when the tab hides — but we keep the already-loaded
    /// tree around so re-opening the tab is instant.
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (value && !_loadedOnce) { _loadedOnce = true; Refresh(); }
        }
    }

    public FileExplorerViewModel(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
        // Deferred: no disk walk until IsActive flips on.
    }

    /// Force a re-read of the root. Useful after the user has created files
    /// outside the app and wants them to show up.
    public void Refresh()
    {
        Roots.Clear();
        if (!Directory.Exists(WorkingDirectory)) return;
        var root = FileNode.CreateRoot(WorkingDirectory);
        root.IsExpanded = true; // eager-expand first level so the tree isn't a single collapsed node
        Roots.Add(root);
        _loadedOnce = true;
        OnChanged(nameof(Roots));
    }

    /// Open a file with the OS default handler. Directories are revealed in
    /// Explorer; files are shell-executed (respects user's default association).
    public static void Open(FileNode node)
    {
        try
        {
            var pInfo = node.IsDirectory
                ? new ProcessStartInfo("explorer.exe", $"\"{node.FullPath}\"") { UseShellExecute = true }
                : new ProcessStartInfo(node.FullPath) { UseShellExecute = true };
            Process.Start(pInfo);
        }
        catch { }
    }

    /// Reveal the node's parent in Explorer with the item pre-selected.
    public static void RevealInExplorer(FileNode node)
    {
        try
        {
            var pInfo = new ProcessStartInfo("explorer.exe", $"/select,\"{node.FullPath}\"") { UseShellExecute = true };
            Process.Start(pInfo);
        }
        catch { }
    }

    /// Copy the file's path to the clipboard.
    public static void CopyPath(FileNode node)
    {
        try
        {
            System.Windows.Clipboard.SetText(node.FullPath);
        }
        catch { }
    }

    /// Copy the file's relative path to the clipboard.
    public static void CopyRelativePath(FileNode node, string rootDir)
    {
        try
        {
            var rel = Path.GetRelativePath(rootDir, node.FullPath).Replace('\\', '/');
            System.Windows.Clipboard.SetText(rel);
        }
        catch { }
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
