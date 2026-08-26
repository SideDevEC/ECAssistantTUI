using ECAssistant.Core;
using ECAssistant.TUI.UI;

namespace ECAssistant.TUI.Session;

/// <summary>
/// Animated loading indicator — writes status messages during startup.
/// In v11.0, this writes to the EGuiConsole's buffered output (which goes
/// to the active layer once InitConsole is called). After InitConsole,
/// the indicator is stopped and the layer's status bar takes over.
/// </summary>
public class LoadingIndicator : IDisposable
{
    private readonly IGuiConsole _gui;
    private readonly EColor _color;
    private Timer? _timer;
    private int _dotCount = 0;
    private string _label = "";
    private bool _running;

    public LoadingIndicator(IGuiConsole gui, EColor color)
    {
        _gui = gui;
        _color = color;
    }

    public void Start(string label)
    {
        _label = label;
        _running = true;
        _dotCount = 0;
        RenderFrame();
        _timer = new Timer(OnTick, null, 500, 500);
    }

    public void UpdateLabel(string label)
    {
        // Clear the current line before changing the label
        _gui.WriteRaw("\r\x1b[2K");
        _label = label;
        _dotCount = 0;
        RenderFrame();
    }

    public void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTick(object? state)
    {
        if (!_running) return;
        RenderFrame();
    }

    private void RenderFrame()
    {
        _dotCount = (_dotCount + 1) % 4;
        var dots = new string('.', _dotCount);
        var line = $"\r{_color.Cyan}{_label}{_color.Reset} {_color.Dim}{dots}  {_color.Reset}";
        // Write to the terminal with carriage return to overwrite the same line.
        // During startup (before InitConsole), this goes to the raw terminal.
        // After InitConsole, the indicator is stopped so this won't interfere.
        _gui.WriteRaw(line);
    }

    public void Dispose() => Stop();
}