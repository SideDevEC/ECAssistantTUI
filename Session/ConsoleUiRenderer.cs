using ECAssistant.UI;

namespace ECAssistant.Session;

/// <summary>
/// Console-based IOutputListener implementation.
///
/// This is the ONLY class outside UI/ that knows about colors.
/// It maps OutputState → ANSI colors and routes through EGuiConsole.
/// Everything upstream just uses ISessionOutput with states.
/// </summary>
public class ConsoleUiRenderer : IOutputListener
{
    private readonly EGuiBase _gui;
    private readonly EColor _color;

    public ConsoleUiRenderer(EGuiBase gui, EColor color)
    {
        _gui = gui;
        _color = color;
    }

    /// <summary>Map output states to ANSI color codes.</summary>
    private string StateToColor(OutputState state) => state switch
    {
        OutputState.Info    => _color.Cyan,
        OutputState.Success => _color.Green,
        OutputState.Warning => _color.Yellow,
        OutputState.Error   => _color.Red,
        OutputState.Dim     => _color.Dim,
        OutputState.Bold    => _color.Bold,
        OutputState.Raw     => _color.Reset,
        OutputState.System  => _color.Magenta,
        _ => _color.Reset
    };

    /// <summary>Map output states to a tag prefix for display.</summary>
    private string? StateToTag(OutputState state) => state switch
    {
        OutputState.Success => "OK",
        OutputState.Warning => "WARN",
        OutputState.Error   => "ERR",
        OutputState.System  => "SYS",
        _ => null
    };

    // ── IOutputListener implementation ──

    public void OnOutput(string text, OutputState state)
    {
        if (string.IsNullOrEmpty(text))
        {
            _gui.BlankLine();
            return;
        }

        var color = StateToColor(state);
        var tag = StateToTag(state);
        if (tag != null)
            _gui.WriteLineColored($"{color}[{tag}] {text}{_color.Reset}");
        else
            _gui.WriteLineColored($"{color}{text}{_color.Reset}");
    }

    public void OnStreamStart() { }
    public void OnStreamStop() { }

    public bool OnRequestApproval(string message)
    {
        var response = _gui.PromptRaw($"{_color.Yellow}{message} [y/N] {_color.Reset}")?.Trim().ToLower();
        return response == "y" || response == "yes";
    }

    /// <summary>Render output history when switching to a session.</summary>
    public void RenderHistory(List<OutputEntry> entries)
    {
        foreach (var entry in entries)
        {
            switch (entry.Type)
            {
                case "stream":
                    if (!string.IsNullOrEmpty(entry.Text))
                    {
                        var c = StateToColor(entry.State);
                        _gui.WriteLineColored(c + entry.Text + _color.Reset);
                    }
                    break;

                case "line":
                    if (string.IsNullOrEmpty(entry.Text))
                        _gui.BlankLine();
                    else
                    {
                        var c = StateToColor(entry.State);
                        var tag = StateToTag(entry.State);
                        if (tag != null)
                            _gui.WriteLineColored($"{c}[{tag}] {entry.Text}{_color.Reset}");
                        else
                            _gui.WriteLineColored($"{c}{entry.Text}{_color.Reset}");
                    }
                    break;
            }
        }
    }
}