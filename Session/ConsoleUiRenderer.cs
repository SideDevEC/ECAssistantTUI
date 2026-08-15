using ECAssistant.UI;
using ECAssistant.Interfaces;

namespace ECAssistant.Session;

/// <summary>
/// Console-based IOutputListener implementation.
///
/// Routes session output through EGuiConsole (which handles ANSI cursor
/// management for the always-visible input line).
///
/// Implements IOutputListener (not the old IUiRenderer) — receives
/// OnOutput, OnStreamStart, OnStreamStop, OnRequestApproval from the session.
/// </summary>
public class ConsoleUiRenderer : IOutputListener
{
    private readonly EGuiBase _gui;
    private readonly IColorFormatter _color;
    private bool _streaming;

    public ConsoleUiRenderer(EGuiBase gui, IColorFormatter color)
    {
        _gui = gui;
        _color = color;
    }

    /// <summary>Map output states to ANSI color codes for the console.</summary>
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
        OutputState.Info    => null,
        OutputState.Success => "OK",
        OutputState.Warning => "WARN",
        OutputState.Error   => "ERR",
        OutputState.Dim     => null,
        OutputState.Bold    => null,
        OutputState.Raw     => null,
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

    public void OnStreamStart()
    {
        _streaming = true;
    }

    public void OnStreamStop()
    {
        _streaming = false;
    }

    public bool OnRequestApproval(string message)
    {
        // Use the GUI's prompt to ask the user
        var response = _gui.PromptRaw($"{_color.Yellow}{message} [y/N] {_color.Reset}")?.Trim().ToLower();
        return response == "y" || response == "yes";
    }

    /// <summary>
    /// Render a full output history (from JSONL file) to the console.
    /// Used when switching to a session to display its complete history.
    /// </summary>
    public void RenderHistory(EGuiBase gui, List<OutputEntry> entries, IColorFormatter color)
    {
        foreach (var entry in entries)
        {
            switch (entry.Type)
            {
                case "stream":
                    if (!string.IsNullOrEmpty(entry.Text))
                    {
                        var c = StateToColor(entry.State);
                        gui.WriteLineColored(c + entry.Text + color.Reset);
                    }
                    break;

                case "line":
                    if (string.IsNullOrEmpty(entry.Text))
                        gui.BlankLine();
                    else
                    {
                        var c = StateToColor(entry.State);
                        var tag = StateToTag(entry.State);
                        if (tag != null)
                            gui.WriteLineColored($"{c}[{tag}] {entry.Text}{color.Reset}");
                        else
                            gui.WriteLineColored($"{c}{entry.Text}{color.Reset}");
                    }
                    break;
            }
        }
    }
}