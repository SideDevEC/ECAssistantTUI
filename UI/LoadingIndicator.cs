using ECAssistant.Core;
using ECAssistant.TUI.UI;

namespace ECAssistant.TUI.Session;

/// <summary>
/// Simple loading indicator — writes the label once, no animation.
/// In ANSI mode, updates are routed through the EGuiConsole layer/render
/// path (replace-last-line) instead of emitting raw \r\x1b[2K escape
/// sequences that would pollute the layer buffer.
/// </summary>
public class LoadingIndicator : IDisposable
{
    private readonly IGuiConsole _gui;
    private readonly EGuiConsole? _egui;
    private readonly EColor _color;
    private string _currentLine = "";

    public LoadingIndicator(IGuiConsole gui, EColor color)
    {
        _gui = gui;
        _egui = gui as EGuiConsole;
        _color = color;
    }

    public void Start(string label)
    {
        _currentLine = $"{_color.Cyan}{label}{_color.Reset}";
        Write(_currentLine);
    }

    public void UpdateLabel(string label)
    {
        var previous = _currentLine;
        _currentLine = $"{_color.Cyan}{label}{_color.Reset}";
        Write(_currentLine, previous);
    }

    public void Stop()
    {
        // Remove the loading line via EGuiConsole (buffering/layer/term path);
        // raw fallback only for foreign IGuiConsole implementations.
        if (_egui != null)
            _egui.ClearLoadingLine(_currentLine);
        else
            _gui.WriteRaw("\r\x1b[2K");
    }

    private void Write(string line, string? previous = null)
    {
        if (_egui != null)
        {
            // Route through the EGuiConsole path — replaces the previous
            // loading line (startup buffer or active layer) instead of raw ANSI.
            _egui.WriteLoadingLine(previous, line);
        }
        else
        {
            if (previous != null)
                _gui.WriteRaw("\r\x1b[2K");
            _gui.WriteLineColored(line);
        }
    }

    public void Dispose() => Stop();
}
