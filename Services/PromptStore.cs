using System.IO;
using System.Windows.Threading;

namespace Looper.Services;

public sealed class PromptStore
{
    private readonly DispatcherTimer _debounce;
    private string _pendingPrompt = "";
    private readonly string _looperDir;
    private readonly string _promptFile;

    public PromptStore(string looperDir, string promptFile)
    {
        _looperDir = looperDir;
        _promptFile = promptFile;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            WritePromptNow(_pendingPrompt);
        };
    }

    public string LoadPrompt()
    {
        try
        {
            return File.Exists(_promptFile) ? File.ReadAllText(_promptFile) : "";
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
            Directory.CreateDirectory(_looperDir);
            var tmp = _promptFile + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, _promptFile, overwrite: true);
        }
        catch { }
    }
}
