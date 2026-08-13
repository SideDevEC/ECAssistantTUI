using static ECAssistant.EColor;

namespace ECAssistant.Session;

/// <summary>
/// Console-based IUiRenderer implementation.
///
/// Attaches to a session and renders output to the console in real-time.
/// Maps OutputState → ANSI colors for the console.
///
/// This is the bridge between the session (which produces output entries)
/// and the console (which displays them). The session doesn't know about
/// colors — this renderer maps states to colors.
/// </summary>
public class ConsoleUiRenderer : IUiRenderer
{
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
                // Raw token from streaming — write inline, no newline
                Console.Write(entry.Text);
                break;

            case "stream":
                // Flushed stream buffer — write as a block
                if (!string.IsNullOrEmpty(entry.Text))
                {
                    var color = StateToColor(entry.State);
                    Console.WriteLine(color + entry.Text + Reset);
                }
                break;

            case "line":
                // Discrete line
                if (string.IsNullOrEmpty(entry.Text))
                {
                    Console.WriteLine(); // blank line
                }
                else
                {
                    var color = StateToColor(entry.State);
                    var tag = StateToTag(entry.State);
                    if (tag != null)
                        Console.WriteLine($"{color}[{tag}] {entry.Text}{Reset}");
                    else
                        Console.WriteLine($"{color}{entry.Text}{Reset}");
                }
                break;
        }
    }

    public void OnQueueChanged(List<string> queue)
    {
        // UI can show queue count — handled by the UI loop, not here
        // to avoid spamming the console. The UI loop polls queue on demand.
    }

    public void OnStateChanged(SessionRunState state)
    {
        // UI can show state changes — handled by the UI loop for display
        // to avoid interrupting the console mid-output. The UI loop polls state.
    }

    /// <summary>
    /// Render a full output history (from JSONL file) to the console.
    /// Used when switching to a session to display its complete history.
    /// </summary>
    public static void RenderHistory(List<OutputEntry> entries)
    {
        Console.Clear(); // clear previous session's display
        foreach (var entry in entries)
        {
            switch (entry.Type)
            {
                case "stream":
                    if (!string.IsNullOrEmpty(entry.Text))
                    {
                        var color = StateToColor(entry.State);
                        Console.WriteLine(color + entry.Text + Reset);
                    }
                    break;

                case "line":
                    if (string.IsNullOrEmpty(entry.Text))
                        Console.WriteLine();
                    else
                    {
                        var color = StateToColor(entry.State);
                        var tag = StateToTag(entry.State);
                        if (tag != null)
                            Console.WriteLine($"{color}[{tag}] {entry.Text}{Reset}");
                        else
                            Console.WriteLine($"{color}{entry.Text}{Reset}");
                    }
                    break;

                // Skip raw_token entries in history — they were already
                // accumulated into "stream" entries via the buffer flush
                case "raw_token":
                    break;
            }
        }
    }
}