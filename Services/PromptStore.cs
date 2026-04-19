using System.IO;
using System.Windows.Threading;
using Looper.Models;

namespace Looper.Services;

public sealed class PromptStore
{
    private readonly DispatcherTimer _debounce;
    private string _pendingPrompt = "";
    private LoopSettings _settings;

    public PromptStore(LoopSettings settings)
    {
        _settings = settings;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            WritePromptNow(_pendingPrompt);
        };
    }

    public void UpdateSettings(LoopSettings settings) => _settings = settings;

    public string LoadPrompt()
    {
        try
        {
            return File.Exists(_settings.PromptFile)
                ? File.ReadAllText(_settings.PromptFile)
                : "";
        }
        catch { return ""; }
    }

    public void SavePromptDebounced(string text)
    {
        _pendingPrompt = text;
        _debounce.Stop();
        _debounce.Start();
    }

    public void FlushPrompt()
    {
        if (_debounce.IsEnabled)
        {
            _debounce.Stop();
            WritePromptNow(_pendingPrompt);
        }
    }

    private void WritePromptNow(string text)
    {
        try
        {
            Directory.CreateDirectory(_settings.LooperDir);
            var tmp = _settings.PromptFile + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, _settings.PromptFile, overwrite: true);
        }
        catch { }
    }
}
