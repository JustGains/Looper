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
                // Fast path: if the icon was already parsed for a previous
                // node with the same extension/folder name, skip the thread
                // pool entirely. A single dictionary lookup instead of a
                // Task.Run + InvokeAsync round-trip — noticeable when the
                // TreeView realizes a large batch of nodes at once.
                bool cached = IsDirectory
                    ? FileIconService.TryGetCachedFolderIcon(Name, _isExpanded, out var cachedImg)
                    : FileIconService.TryGetCachedFileIcon(Name, out cachedImg);
                if (cached && cachedImg != null)
                {
                    _icon = cachedImg;
                    return _icon;
                }
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
            // Guard against stale results if expand/collapse toggled while the
            // icon was loading on the thread pool.
            if (isDir && isExpanded != IsExpanded) return;
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

    private void LoadChildren() => RebuildChildren(preservedExpansions: null);

    /// Re-read the directory from disk while keeping previously-expanded
    /// subdirectories expanded (recursively). Used by the explorer `Refresh`
    /// button so external file creations/deletions show up without
    /// collapsing the user's navigation. Unexpanded subtrees keep their
    /// dummy placeholder so the existing lazy-load path still applies.
    public void ReloadChildren()
    {
        if (!IsDirectory || !_loaded) return;
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpandedDescendants(this, expanded);
        RebuildChildren(expanded);
    }

    private static void CollectExpandedDescendants(FileNode node, HashSet<string> into)
    {
        foreach (var child in node.Children)
        {
            if (child.IsDirectory && child.IsExpanded)
            {
                into.Add(child.FullPath);
                CollectExpandedDescendants(child, into);
            }
        }
    }

    private void RebuildChildren(HashSet<string>? preservedExpansions)
    {
        Children.Clear();
        try
        {
            // List.Sort + StringComparer.OrdinalIgnoreCase is cheaper than
            // LINQ OrderBy here — OrderBy would allocate a lookup table and
            // project each element, List<T>.Sort does it in place with a
            // single Comparer delegate.
            var dirs = Directory.GetDirectories(FullPath);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d) ?? "";
                if (IsSkipped(name)) continue;
                var child = new FileNode
                {
                    Name = name,
                    FullPath = d,
                    IsDirectory = true,
                };
                child.Children.Add(DummyChild);
                Children.Add(child);

                if (preservedExpansions != null &&
                    preservedExpansions.Contains(child.FullPath))
                {
                    // Bypass the IsExpanded setter's default LoadChildren call
                    // so we can recurse into the rebuild with the same set and
                    // keep deep expansion state intact.
                    child._isExpanded = true;
                    child.PropertyChanged?.Invoke(child, new PropertyChangedEventArgs(nameof(IsExpanded)));
                    child.RebuildChildren(preservedExpansions);
                }
            }

            var files = Directory.GetFiles(FullPath);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                Children.Add(new FileNode
                {
                    Name = Path.GetFileName(f) ?? f,
                    FullPath = f,
                    IsDirectory = false,
                });
            }
            _loaded = true;
        }
        catch
        {
            // Permission denied / race on deletion — leave the empty list.
        }
    }

    /// Directory-name blocklist delegated to the shared helper so the file
    /// explorer and package-json discovery never drift out of sync.
    private static bool IsSkipped(string name) => DirectorySkipList.ShouldHideInTree(name);
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
            if (value && !_loadedOnce) { _loadedOnce = true; HardRefresh(); }
        }
    }

    public FileExplorerViewModel(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
        // Deferred: no disk walk until IsActive flips on.
    }

    /// Re-read the tree from disk while keeping every expanded folder open.
    /// Default behaviour of the Files-pane Refresh button — external file
    /// creations/deletions show up without collapsing the user's navigation.
    public void Refresh()
    {
        if (Roots.Count == 0) { HardRefresh(); return; }
        foreach (var root in Roots) root.ReloadChildren();
    }

    /// Wipe and rebuild from scratch. Used on first activation and when the
    /// user explicitly wants a fresh tree (Shift+click on the Refresh button).
    public void HardRefresh()
    {
        Roots.Clear();
        if (!Directory.Exists(WorkingDirectory)) return;
        var root = FileNode.CreateRoot(WorkingDirectory);
        root.IsExpanded = true; // eager-expand first level so the tree isn't a single collapsed node
        Roots.Add(root);
        _loadedOnce = true;
        OnChanged(nameof(Roots));
    }

    /// Collapse every currently-expanded directory in the tree, keeping only
    /// the root nodes open. The user's loaded data isn't dropped — re-expand
    /// is instant because children are still in memory.
    public void CollapseAll()
    {
        foreach (var r in Roots)
        {
            // Collapse every descendant; leave the root itself expanded so
            // the pane doesn't look empty after the click.
            foreach (var child in r.Children) CollapseRecursive(child);
        }
    }

    private static void CollapseRecursive(FileNode n)
    {
        if (!n.IsDirectory) return;
        foreach (var c in n.Children) CollapseRecursive(c);
        if (n.IsExpanded) n.IsExpanded = false;
    }

    /// Reveal the node's parent in Explorer with the item pre-selected.
    public static void RevealInExplorer(FileNode node)
        => ShellPathActions.RevealInExplorer(node.FullPath);

    /// Open a file with the OS default handler. Directories are revealed in
    /// Explorer; files are shell-executed (respects user's default association).
    public static void Open(FileNode node)
        => ShellPathActions.Open(node.FullPath);

    /// Copy the file's path to the clipboard.
    public static void CopyPath(FileNode node) => ShellPathActions.CopyPath(node.FullPath);

    /// Copy the file's relative path to the clipboard.
    public static void CopyRelativePath(FileNode node, string rootDir)
    {
        try
        {
            var rel = Path.GetRelativePath(rootDir, node.FullPath).Replace('\\', '/');
            ShellPathActions.CopyText(rel);
        }
        catch { }
    }

    /// Copy the file's name (leaf) to the clipboard.
    public static void CopyFileName(FileNode node) => ShellPathActions.CopyText(node.Name);

    /// Copy the text content of a file to the clipboard (skip if too large).
    public static void CopyFileContent(FileNode node, int maxBytes = 100_000)
    {
        try
        {
            if (node.IsDirectory) return;
            var info = new FileInfo(node.FullPath);
            if (!info.Exists || info.Length > maxBytes) return;
            var text = File.ReadAllText(node.FullPath);
            ShellPathActions.CopyText(text);
        }
        catch { }
    }

    /// Open Windows Terminal in the node's directory.
    public static void OpenTerminalHere(FileNode node)
        => ShellPathActions.OpenTerminalHere(node.FullPath);

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
