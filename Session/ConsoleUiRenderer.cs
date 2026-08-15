using ECAssistant.UI;
using ECAssistant.Interfaces;

namespace ECAssistant.Session;

/// <summary>
/// Console-based IUiRenderer implementation.
///
/// Routes session output through EGuiConsole (which handles ANSI scroll region
/// cursor management). Without this routing, Console.Write would bypass the
/// scroll region and garble the input line.
/// </summary>
public class ConsoleUiRenderer : IUiRenderer
{
    private readonly EGuiBase _gui;
    private readonly IColorFormatter _color;

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

    public void OnRawDirect(string token)
    {
        _gui.WriteRawDirect(token);
    }

    public void OnOutput(OutputEntry entry)
    {
        switch (entry.Type)
        {
            case "raw_token":
                // No longer sent — tokens batch in session _streamBuffer
                // and arrive as "stream" entries on flush
                break;

            case "stream":
                // Flushed stream buffer — write as a block
                // v10.22: Tool output uses Dim color to distinguish from LLM output
                if (!string.IsNullOrEmpty(entry.Text))
                {
                    var color = StateToColor(entry.State);
                    _gui.WriteLineColored(color + entry.Text + _color.Reset);
                }
                break;

            case "tool_output":
                // v10.22: Tool results in dim gray for visual distinction
                if (!string.IsNullOrEmpty(entry.Text))
                {
                    _gui.WriteLineColored(_color.Dim + entry.Text + _color.Reset);
                }
                break;

            case "thinking":
                // v10.22: LLM thinking in italic/dim
                if (!string.IsNullOrEmpty(entry.Text))
                {
                    _gui.WriteLineColored(_color.Dim + "💭 " + entry.Text + _color.Reset);
                }
                break;

            case "line":
                // Discrete line
                if (string.IsNullOrEmpty(entry.Text))
                {
                    _gui.BlankLine();
                }
                else
                {
                    var color = StateToColor(entry.State);
                    var tag = StateToTag(entry.State);
                    if (tag != null)
                        _gui.WriteLineColored($"{color}[{tag}] {entry.Text}{_color.Reset}");
                    else
                        _gui.WriteLineColored($"{color}{entry.Text}{_color.Reset}");
                }
                break;
        }
    }

    public void OnQueueChanged(List<string> queue)
    {
        // Queue display handled by UI loop — not here to avoid console spam
    }

    public void OnStateChanged(SessionRunState state)
    {
        // State display handled by UI loop
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
                        var c = entry.State switch
                        {
                            OutputState.Info    => color.Cyan,
                            OutputState.Success => color.Green,
                            OutputState.Warning => color.Yellow,
                            OutputState.Error   => color.Red,
                            OutputState.Dim     => color.Dim,
                            OutputState.Bold    => color.Bold,
                            OutputState.Raw     => color.Reset,
                            OutputState.System  => color.Magenta,
                            _ => color.Reset
                        };
                        gui.WriteLineColored(c + entry.Text + color.Reset);
                    }
                    break;

                case "line":
                    if (string.IsNullOrEmpty(entry.Text))
                        gui.BlankLine();
                    else
                    {
                        var c = entry.State switch
                        {
                            OutputState.Info    => color.Cyan,
                            OutputState.Success => color.Green,
                            OutputState.Warning => color.Yellow,
                            OutputState.Error   => color.Red,
                            OutputState.Dim     => color.Dim,
                            OutputState.Bold    => color.Bold,
                            OutputState.Raw     => color.Reset,
                            OutputState.System  => color.Magenta,
                            _ => color.Reset
                        };
                        var tag = entry.State switch
                        {
                            OutputState.Success => "OK",
                            OutputState.Warning => "WARN",
                            OutputState.Error   => "ERR",
                            OutputState.System  => "SYS",
                            _ => null
                        };
                        if (tag != null)
                            gui.WriteLineColored($"{c}[{tag}] {entry.Text}{color.Reset}");
                        else
                            gui.WriteLineColored($"{c}{entry.Text}{color.Reset}");
                    }
                    break;

                case "raw_token":
                    break;
            }
        }
    }
}