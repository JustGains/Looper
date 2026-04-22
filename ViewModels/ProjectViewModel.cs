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
            var trimmed = WorkingDirectory.TrimEnd('\\', '/');
            var leaf = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
        }
    }
    public string HeaderTooltip => WorkingDirectory;

    public ObservableCollection<ConversationViewModel> Conversations { get; } = new();

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

    public ProjectViewModel(string workingDirectory, LoopSettings shellDefaults)
    {
        WorkingDirectory = ConfigStore.Normalize(workingDirectory);
        _shellDefaults = shellDefaults;

        // Migration from legacy per-dir files (if they exist).
        ConversationStore.MigrateLegacyIfNeeded(WorkingDirectory, shellDefaults);

        _projectConfig = ConversationStore.LoadProject(WorkingDirectory);

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
        };

        // Pick the last-used conversation if possible
        _selectedConversation = Conversations.FirstOrDefault(c =>
            string.Equals(c.Id, _projectConfig.LastConversationId, StringComparison.OrdinalIgnoreCase))
            ?? Conversations.FirstOrDefault();

        // Persist the cleaned order
        _projectConfig.ConversationOrder = Conversations.Select(c => c.Id).ToList();
        _projectConfig.LastConversationId = _selectedConversation?.Id;
        SaveProjectConfig();
    }

    private void OnConvPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationViewModel.IsRunning))
            OnChanged(nameof(IsRunning));
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
        _projectConfig.LastConversationId = SelectedConversation?.Id;
        _projectConfig.ConversationOrder = Conversations.Select(c => c.Id).ToList();
        SaveProjectConfig();
    }

    public void Dispose() => Shutdown();

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
