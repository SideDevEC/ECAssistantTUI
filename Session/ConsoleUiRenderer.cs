using static ECAssistant.EColor;
using ECAssistant.UI;

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

    public ConsoleUiRenderer(EGuiBase gui)
    {
        _gui = gui;
    }

    /// <summary>Map output states to ANSI color codes for the console.</summary>
    private static string StateToColor(OutputState state) => state switch
    {
        OutputState.Info    => Cyan,
        OutputState.Success => Success(),
        OutputState.Warning => Warn(),
        OutputState.Error   => Error(),
        OutputState.Dim     => Dim,
        OutputState.Bold    => Bold,
        OutputState.Raw     => Reset,
        OutputState.System  => Magenta,
        _ => Reset
    };

    /// <summary>Map output states to a tag prefix for display.</summary>
    private static string? StateToTag(OutputState state) => state switch
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
                    _gui.WriteLineColored(color + entry.Text + Reset);
                }
                break;

            case "tool_output":
                // v10.22: Tool results in dim gray for visual distinction
                if (!string.IsNullOrEmpty(entry.Text))
                {
                    _gui.WriteLineColored(Dim + entry.Text + Reset);
                }
                break;

            case "thinking":
                // v10.22: LLM thinking in italic/dim
                if (!string.IsNullOrEmpty(entry.Text))
                {
                    _gui.WriteLineColored(Dim + "💭 " + entry.Text + Reset);
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
                        _gui.WriteLineColored($"{color}[{tag}] {entry.Text}{Reset}");
                    else
                        _gui.WriteLineColored($"{color}{entry.Text}{Reset}");
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
    public static void RenderHistory(EGuiBase gui, List<OutputEntry> entries)
    {
        foreach (var entry in entries)
        {
            switch (entry.Type)
            {
                case "stream":
                    if (!string.IsNullOrEmpty(entry.Text))
                    {
                        var color = StateToColor(entry.State);
                        gui.WriteLineColored(color + entry.Text + Reset);
                    }
                    break;

                case "line":
                    if (string.IsNullOrEmpty(entry.Text))
                        gui.BlankLine();
                    else
                    {
                        var color = StateToColor(entry.State);
                        var tag = StateToTag(entry.State);
                        if (tag != null)
                            gui.WriteLineColored($"{color}[{tag}] {entry.Text}{Reset}");
                        else
                            gui.WriteLineColored($"{color}{entry.Text}{Reset}");
                    }
                    break;

                case "raw_token":
                    break;
            }
        }
    }
}