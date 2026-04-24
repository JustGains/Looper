using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using JustCode.Models;
using JustCode.Services;

namespace JustCode.ViewModels;

public sealed class ProjectViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<ConversationViewModel>? ConversationAdded;
    public event EventHandler<ConversationViewModel>? ConversationRemoved;

    private readonly LoopSettings _shellDefaults;
    private ProjectConfig _projectConfig;

    public string WorkingDirectory { get; }

    public string HeaderLabel
    {
        get
        {
            var trimmed = WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
        }
    }
    public string HeaderTooltip => WorkingDirectory;

    public ObservableCollection<ConversationViewModel> Conversations { get; } = new();

    /// Filtered view over `Conversations` driven by <see cref="ConversationFilter"/>.
    /// The XAML `ListBox` binds to this instead of the raw collection so that
    /// typing in the filter box hides non-matching rows without disturbing
    /// the underlying order (and therefore the persisted ConversationOrder).
    public System.ComponentModel.ICollectionView ConversationsView { get; private set; } = null!;

    private string _conversationFilter = "";
    public string ConversationFilter
    {
        get => _conversationFilter;
        set
        {
            var v = value ?? "";
            if (_conversationFilter == v) return;
            _conversationFilter = v;
            ConversationsView.Refresh();
            OnChanged();
            OnChanged(nameof(HasConversationFilter));
            OnChanged(nameof(FilterMatchedNothing));
        }
    }

    public bool HasConversationFilter => !string.IsNullOrEmpty(_conversationFilter);
    /// True iff the user typed a filter that no conversation name matches —
    /// the XAML uses this to surface a gentle "no matches" placeholder.
    public bool FilterMatchedNothing =>
        HasConversationFilter && !Conversations.Any(FilterAccepts);

    private bool FilterAccepts(object? item) =>
        item is ConversationViewModel c && FilterAccepts(c);
    private bool FilterAccepts(ConversationViewModel c)
    {
        if (!HasConversationFilter) return true;
        return c.Name?.IndexOf(_conversationFilter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public void ClearConversationFilter() => ConversationFilter = "";

    private ConversationViewModel? _selectedConversation;
    public ConversationViewModel? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (ReferenceEquals(_selectedConversation, value)) return;
            _selectedConversation = value;
            _projectConfig.LastConversationId = value?.Id;
            SaveProjectConfig();
            OnChanged();
            OnChanged(nameof(IsRunning));
        }
    }

    /// True if any conversation is running. Used for the project tab header indicator.
    public bool IsRunning => Conversations.Any(c => c.IsRunning);

    /// Cached package.json discovery — refreshed on demand when the launch
    /// button is clicked so we don't rescan during steady-state use.
    private IReadOnlyList<PackageInfo>? _packages;
    public IReadOnlyList<PackageInfo> DiscoverPackages()
    {
        _packages = PackageJsonService.Discover(WorkingDirectory);
        return _packages;
    }

    public bool HasRootPackageJson => File.Exists(Path.Combine(WorkingDirectory, "package.json"));

    /// File-explorer panel. Built lazily the first time the tab is shown so
    /// cold-startup of the app doesn't walk disk for every open project.
    private FileExplorerViewModel? _fileExplorer;
    public FileExplorerViewModel FileExplorer =>
        _fileExplorer ??= new FileExplorerViewModel(WorkingDirectory);

    /// Git panel. Lazily built on first access; keeps a FS watcher on .git
    /// and a periodic poll so the sidebar reflects external changes.
    private GitViewModel? _git;
    public GitViewModel Git =>
        _git ??= new GitViewModel(WorkingDirectory,
            activeConvSettings: () => SelectedConversation?.SettingsSnapshot);

    public bool IsGitRepo => GitService.IsRepo(WorkingDirectory);

    /// Which panel is active in the activity-bar sidebar. `"files"` / `"conversations"` / `"git"`.
    /// Persisted across app sessions via ProjectConfig.LastSidebarTab.
    public string SidebarTab
    {
        get => _projectConfig.LastSidebarTab ?? "conversations";
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? "conversations" : value;
            if (_projectConfig.LastSidebarTab == v) return;
            _projectConfig.LastSidebarTab = v;
            SaveProjectConfig();
            OnChanged();
            OnChanged(nameof(IsFilesTab));
            OnChanged(nameof(IsConversationsTab));
            OnChanged(nameof(IsGitTab));
            UpdateTabActivations();
        }
    }

    /// Gate the Git + FileExplorer panels so they only poll / watch / walk
    /// disk when their tab is the current one AND this project is selected.
    /// Inactive tabs cost ~nothing — they're just held collections. Call this
    /// from the MainVM when SelectedProject changes and from SidebarTab's
    /// setter when the user clicks a different activity-bar icon.
    public bool IsSelectedProject { get; private set; }
    public void SetIsSelected(bool selected)
    {
        if (IsSelectedProject == selected) return;
        IsSelectedProject = selected;
        UpdateTabActivations();
    }

    private void UpdateTabActivations()
    {
        bool wantGit = IsSelectedProject && IsGitTab;
        bool wantFiles = IsSelectedProject && IsFilesTab;
        // Only create the VM if we're actually going to use it — the `Git` /
        // `FileExplorer` getters are lazy. Touching them eagerly would force
        // the VM to exist even for projects whose tabs never get opened.
        if (wantGit) Git.IsActive = true;
        else if (_git != null) _git.IsActive = false;
        if (wantFiles) FileExplorer.IsActive = true;
        else if (_fileExplorer != null) _fileExplorer.IsActive = false;
    }

    public bool IsFilesTab => SidebarTab == "files";
    public bool IsConversationsTab => SidebarTab == "conversations";
    public bool IsGitTab => SidebarTab == "git";

    /// VS Code-style multi-session terminal panel scoped to this project.
    /// Created after WorkingDirectory is known so sessions spawn in the right
    /// cwd. Disposed on project shutdown.
    public TerminalPanelViewModel TerminalPanel { get; private set; } = null!;

    public ProjectViewModel(string workingDirectory, LoopSettings shellDefaults)
    {
        WorkingDirectory = ConfigStore.Normalize(workingDirectory);
        _shellDefaults = shellDefaults;

        // Migration from legacy per-dir files (if they exist).
        ConversationStore.MigrateLegacyIfNeeded(WorkingDirectory, shellDefaults);

        _projectConfig = ConversationStore.LoadProject(WorkingDirectory);
        TerminalPanel = new TerminalPanelViewModel(
            () => WorkingDirectory,
            () => _shellDefaults.DefaultShellId);

        // Load conversations
        var foundIds = ConversationStore.EnumerateConversationIds(WorkingDirectory);

        // Determine order: ConversationOrder first (if still present), then any stragglers
        var orderedIds = _projectConfig.ConversationOrder
            .Where(id => foundIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Concat(foundIds.Where(id => !_projectConfig.ConversationOrder.Contains(id, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        if (orderedIds.Count == 0)
        {
            // Seed a brand-new default conversation
            var cfg = ConversationStore.SeedFromDefaults(ConversationStore.NewId(), "Default", shellDefaults);
            ConversationStore.SaveConversation(WorkingDirectory, cfg);
            orderedIds.Add(cfg.Id);
        }

        foreach (var id in orderedIds)
            Conversations.Add(BuildConversationVM(id));

        // Wire collection-level running state signal
        foreach (var c in Conversations) c.PropertyChanged += OnConvPropertyChanged;
        Conversations.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ConversationViewModel c in e.NewItems) c.PropertyChanged += OnConvPropertyChanged;
            if (e.OldItems != null)
                foreach (ConversationViewModel c in e.OldItems) c.PropertyChanged -= OnConvPropertyChanged;
            OnChanged(nameof(FilterMatchedNothing));
        };

        // Shared filtered view used by the conversations ListBox. Live filtering
        // reacts to ConversationFilter changes via the setter's `Refresh()`.
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Conversations);
        view.Filter = FilterAccepts;
        ConversationsView = view;

        // Pick the last-used conversation if possible
        _selectedConversation = Conversations.FirstOrDefault(c =>
            string.Equals(c.Id, _projectConfig.LastConversationId, StringComparison.OrdinalIgnoreCase))
            ?? Conversations.FirstOrDefault();

        // Float any pre-pinned items to the top (ConversationOrder gets
        // rewritten to reflect pinned-first so subsequent loads skip this).
        ResortConversations();

        // Persist the cleaned order
        _projectConfig.ConversationOrder = Conversations.Select(c => c.Id).ToList();
        _projectConfig.LastConversationId = _selectedConversation?.Id;
        SaveProjectConfig();
    }

    private void OnConvPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationViewModel.IsRunning))
            OnChanged(nameof(IsRunning));
        else if (e.PropertyName == nameof(ConversationViewModel.IsPinned))
            ResortConversations();
        else if (e.PropertyName == nameof(ConversationViewModel.Name))
        {
            // A rename can change whether the active filter matches this row.
            // Cheap refresh — the view is already over an in-memory list.
            if (HasConversationFilter) ConversationsView?.Refresh();
            OnChanged(nameof(FilterMatchedNothing));
        }
    }

    /// Stable sort by (IsPinned desc, original order). Pinned conversations
    /// float to the top while the unpinned block keeps the order the user
    /// curated on disk (ConversationOrder). Runs in-place on the existing
    /// ObservableCollection via `Move` so WPF's ListBox keeps its item
    /// containers and selection rather than tearing down the virtualized
    /// list.
    private bool _resorting;
    private void ResortConversations()
    {
        if (_resorting) return;

        // Scan for the first position where pinned/unpinned order is violated.
        // The expected shape is: pinned prefix, then unpinned tail, with the
        // internal order within each block unchanged. If we never see a
        // pinned chip after an unpinned chip, the list is already sorted
        // and we can skip the O(N²) Move loop + a ConfigStore save round-trip.
        int n = Conversations.Count;
        bool sawUnpinned = false;
        bool needsSort = false;
        for (int i = 0; i < n; i++)
        {
            if (Conversations[i].IsPinned)
            {
                if (sawUnpinned) { needsSort = true; break; }
            }
            else sawUnpinned = true;
        }
        if (!needsSort) return;

        _resorting = true;
        try
        {
            // Stable partition: pinned prefix retains its relative order,
            // unpinned tail retains its relative order. Two Select+idx
            // passes avoid the OrderBy allocation for a simple bool key.
            var pinned = Conversations.Where(c => c.IsPinned).ToList();
            var unpinned = Conversations.Where(c => !c.IsPinned).ToList();
            var desired = new List<ConversationViewModel>(n);
            desired.AddRange(pinned);
            desired.AddRange(unpinned);
            for (int i = 0; i < desired.Count; i++)
            {
                if (ReferenceEquals(Conversations[i], desired[i])) continue;
                var from = Conversations.IndexOf(desired[i]);
                if (from >= 0 && from != i) Conversations.Move(from, i);
            }
            _projectConfig.ConversationOrder = Conversations.Select(c => c.Id).ToList();
            SaveProjectConfig();
        }
        finally { _resorting = false; }
    }

    private ConversationViewModel BuildConversationVM(string id)
    {
        var cfg = ConversationStore.LoadConversation(WorkingDirectory, id);
        // Ensure settings file exists on disk (fresh project writes defaults).
        if (!File.Exists(ConversationStore.ConversationSettingsFile(WorkingDirectory, id)))
            ConversationStore.SaveConversation(WorkingDirectory, cfg);

        var vm = new ConversationViewModel(WorkingDirectory, cfg,
            persistSettings: () => ConversationStore.SaveConversation(WorkingDirectory, cfg));
        return vm;
    }

    /// Fork an existing conversation from its current position. The new
    /// conversation inherits the source's settings, prompt, and task list,
    /// and its first run will use the per-tool fork flag so the parent's
    /// session isn't mutated.
    public ConversationViewModel ForkConversation(ConversationViewModel source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        var src = ConversationStore.LoadConversation(WorkingDirectory, source.Id);

        var newName = UniqueForkName(source.Name);
        var cfg = new ConversationSettings
        {
            Id = ConversationStore.NewId(),
            Name = newName,
            Tool = src.Tool,
            TimeoutSeconds = src.TimeoutSeconds,
            MaxIterations = src.MaxIterations,
            RalphEnabled = src.RalphEnabled,
            KeepContext = src.KeepContext,
            ClaudeModel = src.ClaudeModel,
            ClaudeEffort = src.ClaudeEffort,
            CodexModel = src.CodexModel,
            CodexEffort = src.CodexEffort,
            PiModel = src.PiModel,
            PiThinking = src.PiThinking,
            EnabledSkills = new List<string>(src.EnabledSkills),
            MentionMap = new Dictionary<string, string>(src.MentionMap, StringComparer.OrdinalIgnoreCase),
            // Key piece: next run forks from the parent's current session id.
            // Cleared automatically once the CLI captures the forked session's
            // new id.
            PendingForkFromSessionId = string.IsNullOrEmpty(src.LastSessionId) ? null : src.LastSessionId,
            LastSessionId = null,
            LastSessionTimestamp = null,
        };
        ConversationStore.SaveConversation(WorkingDirectory, cfg);

        // Seed the forked conversation with the parent's prompt + tasks so
        // it starts where the user left off.
        TryCopy(ConversationStore.PromptFile(WorkingDirectory, source.Id),
                ConversationStore.PromptFile(WorkingDirectory, cfg.Id));
        TryCopy(ConversationStore.TasksFile(WorkingDirectory, source.Id),
                ConversationStore.TasksFile(WorkingDirectory, cfg.Id));

        var vm = new ConversationViewModel(WorkingDirectory, cfg,
            persistSettings: () => ConversationStore.SaveConversation(WorkingDirectory, cfg));
        Conversations.Add(vm);
        ConversationAdded?.Invoke(this, vm);

        _projectConfig.ConversationOrder = Conversations.Select(c => c.Id).ToList();
        SaveProjectConfig();
        SelectedConversation = vm;
        return vm;
    }

    private string UniqueForkName(string baseName)
    {
        var existing = new HashSet<string>(Conversations.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var candidate = $"{baseName} (fork)";
        if (!existing.Contains(candidate)) return candidate;
        for (int i = 2; ; i++)
        {
            var next = $"{baseName} (fork {i})";
            if (!existing.Contains(next)) return next;
        }
    }

    private static void TryCopy(string src, string dst)
    {
        try
        {
            if (!File.Exists(src)) return;
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, dst, overwrite: true);
        }
        catch { }
    }

    public ConversationViewModel AddConversation()
    {
        var existingNames = Conversations.Select(c => c.Name).ToList();
        var name = ConversationStore.SuggestName(WorkingDirectory, existingNames);
        var cfg = ConversationStore.SeedFromDefaults(ConversationStore.NewId(), name, _shellDefaults);
        ConversationStore.SaveConversation(WorkingDirectory, cfg);

        var vm = new ConversationViewModel(WorkingDirectory, cfg,
            persistSettings: () => ConversationStore.SaveConversation(WorkingDirectory, cfg));
        Conversations.Add(vm);
        ConversationAdded?.Invoke(this, vm);

        _projectConfig.ConversationOrder = Conversations.Select(c => c.Id).ToList();
        SaveProjectConfig();

        SelectedConversation = vm;
        return vm;
    }

    /// Create a conversation pre-bound to an existing CLI session id so the
    /// first Start/Send resumes that conversation instead of opening a new
    /// one. Used by the "import session" UI to bring in a thread that was
    /// started outside the app.
    public ConversationViewModel ImportConversationWithSession(string sessionId)
    {
        var vm = AddConversation();
        vm.ImportSessionId(sessionId);
        return vm;
    }

    public void RemoveConversation(ConversationViewModel vm)
    {
        if (vm == null || !Conversations.Contains(vm)) return;
        if (Conversations.Count <= 1) return; // keep at least one

        var wasSelected = ReferenceEquals(SelectedConversation, vm);
        var idx = Conversations.IndexOf(vm);
        vm.Shutdown();
        Conversations.Remove(vm);
        ConversationRemoved?.Invoke(this, vm);
        ConversationStore.DeleteConversation(WorkingDirectory, vm.Id);
        vm.Dispose();

        _projectConfig.ConversationOrder = Conversations.Select(c => c.Id).ToList();
        if (wasSelected)
            SelectedConversation = Conversations[Math.Max(0, Math.Min(idx - 1, Conversations.Count - 1))];
        SaveProjectConfig();
    }

    public bool CanRemoveSelected => Conversations.Count > 1;

    private void SaveProjectConfig() =>
        ConversationStore.SaveProject(WorkingDirectory, _projectConfig);

    public void Shutdown()
    {
        foreach (var c in Conversations.ToList()) c.Shutdown();
        try { TerminalPanel?.Dispose(); } catch { }
        _projectConfig.LastConversationId = SelectedConversation?.Id;
        _projectConfig.ConversationOrder = Conversations.Select(c => c.Id).ToList();
        SaveProjectConfig();
    }

    public void Dispose() => Shutdown();

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
