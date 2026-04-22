using System.ComponentModel;
using JustCode.Services;

namespace JustCode.ViewModels;

/// Row in the skills menu — wraps a discovered SkillEntry with a persistent
/// IsEnabled toggle. The conversation VM listens to `Toggled` to keep its
/// EnabledSkills list in sync and persist to disk.
public sealed class SkillPick : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Toggled;

    public SkillEntry Entry { get; }

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            Toggled?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Name => Entry.Name;
    public string Origin => Entry.Origin;
    public string Description => Entry.Description;
    public string Path => Entry.Path;

    public SkillPick(SkillEntry entry, bool enabled)
    {
        Entry = entry;
        _isEnabled = enabled;
    }
}
