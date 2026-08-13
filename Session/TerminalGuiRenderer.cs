using ECAssistant.UI;

namespace ECAssistant.Session;

/// <summary>
/// Terminal.Gui-based IUiRenderer implementation.
///
/// Routes session output (tokens, lines, streams) through the EGuiTerminal
/// output view via Application.MainLoop.Invoke (thread-safe).
/// This replaces ConsoleUiRenderer for the Terminal.Gui UI.
/// </summary>
public class TerminalGuiRenderer : IUiRenderer
{
    private readonly EGuiTerminal _gui;

    public TerminalGuiRenderer(EGuiTerminal gui)
    {
        _gui = gui;
    }

    public void OnOutput(OutputEntry entry)
    {
        switch (entry.Type)
        {
            case "raw_token":
                // Raw token from streaming — write inline, no newline
                _gui.WriteRaw(entry.Text);
                break;

            case "stream":
                if (!string.IsNullOrEmpty(entry.Text))
                {
                    _gui.WriteLine(entry.Text);
                }
                break;

            case "line":
                if (string.IsNullOrEmpty(entry.Text))
                {
                    _gui.BlankLine();
                }
                else
                {
                    // Simple tag formatting without ANSI (Terminal.Gui handles colors)
                    var tag = StateToTag(entry.State);
                    if (tag != null)
                        _gui.WriteLine($"[{tag}] {entry.Text}");
                    else
                        _gui.WriteLine(entry.Text);
                }
                break;
        }
    }

    public void OnQueueChanged(List<string> queue)
    {
        // Could update status bar with queue count
    }

    public void OnStateChanged(SessionRunState state)
    {
        _gui.SetState(state.ToString());
    }

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
}