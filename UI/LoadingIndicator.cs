using ECAssistant.Core;
using ECAssistant.TUI.UI;

namespace ECAssistant.TUI.Session;

/// <summary>
/// Simple loading indicator — writes the label once, no animation.
/// </summary>
public class LoadingIndicator : IDisposable
{
    private readonly IGuiConsole _gui;
    private readonly EColor _color;
    private string _currentLine = "";

    public LoadingIndicator(IGuiConsole gui, EColor color)
    {
        _gui = gui;
        _color = color;
    }

    public void Start(string label)
    {
        _currentLine = $"{_color.Cyan}{label}{_color.Reset}";
        _gui.WriteLineColored(_currentLine);
    }

    public void UpdateLabel(string label)
    {
        // Clear previous line and write new one
        _gui.WriteRaw("\r\x1b[2K");
        _currentLine = $"{_color.Cyan}{label}{_color.Reset}";
        _gui.WriteLineColored(_currentLine);
    }

    public void Stop()
    {
        // Clear the loading line
        _gui.WriteRaw("\r\x1b[2K");
    }

    public void Dispose() => Stop();
}
