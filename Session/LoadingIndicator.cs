using static ECAssistant.EColor;
using ECAssistant.UI;

namespace ECAssistant.Session;

/// <summary>
/// Animated loading indicator that shows . .. ... in a loop at the prompt position.
/// Runs on a background timer (500ms interval). Input is disabled while running.
/// 
/// Usage:
///   var indicator = new LoadingIndicator(Gui);
///   indicator.Start("Initializing session 'main'");
///   ... do async work ...
///   indicator.Stop();  // clears the line, ready for prompt
/// </summary>
public class LoadingIndicator : IDisposable
{
    private readonly EGuiBase _gui;
    private Timer? _timer;
    private int _dotCount = 0;
    private string _label = "";
    private bool _running;

    public LoadingIndicator(EGuiBase gui)
    {
        _gui = gui;
    }

    /// <summary>Start the animated indicator with a label.</summary>
    public void Start(string label)
    {
        _label = label;
        _running = true;
        _dotCount = 0;

        // Print initial line
        RenderFrame();

        // Start timer — 500ms interval
        _timer = new Timer(OnTick, null, 500, 500);
    }

    /// <summary>Update the label (e.g. "Initializing session 'watcher'...").</summary>
    public void UpdateLabel(string label)
    {
        _label = label;
        // Force re-render on next tick
    }

    /// <summary>Stop the indicator and clear the line.</summary>
    public void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;

        // Clear the current line: \r + spaces + \r
        _gui.WriteRawDirect("\r" + new string(' ', Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80) + "\r");
    }

    private void OnTick(object? state)
    {
        if (!_running) return;
        RenderFrame();
    }

    private void RenderFrame()
    {
        _dotCount = (_dotCount + 1) % 4; // 0, 1, 2, 3
        var dots = new string('.', _dotCount);

        // \r to return to start of line, then overwrite
        var line = $"\r{Cyan}{_label}{Reset} {Dim}{dots}  {Reset}";
        _gui.WriteRawDirect(line);
    }

    public void Dispose()
    {
        Stop();
    }
}