namespace ECAssistant.Session;

/// <summary>
/// Output states — semantic meaning of output, not colors.
/// The UI maps these to colors/styles however it wants.
/// </summary>
public enum OutputState
{
    /// <summary>Informational message</summary>
    Info,
    /// <summary>Operation succeeded</summary>
    Success,
    /// <summary>Caution / non-critical</summary>
    Warning,
    /// <summary>Failure / critical</summary>
    Error,
    /// <summary>De-emphasized text</summary>
    Dim,
    /// <summary>Emphasized text</summary>
    Bold,
    /// <summary>Plain text / token stream (no styling)</summary>
    Raw,
    /// <summary>System-level message</summary>
    System
}

/// <summary>
/// A single output entry — one line in the JSONL output buffer file.
/// Serialized as JSON, one per line.
/// </summary>
public class OutputEntry
{
    /// <summary>"stream" (accumulated tokens) or "line" (a discrete line)</summary>
    public string Type { get; set; } = "line";

    /// <summary>The output text content</summary>
    public string Text { get; set; } = "";

    /// <summary>Output state (Info, Success, Warning, etc.)</summary>
    public OutputState State { get; set; } = OutputState.Raw;

    /// <summary>ISO-8601 timestamp</summary>
    public string Ts { get; set; } = DateTime.UtcNow.ToString("O");
}

/// <summary>
/// Interface for the UI renderer attached to a session.
/// The console, canvas, or test harness implements this.
/// The session calls these methods when output is produced or state changes.
/// </summary>
public interface IUiRenderer
{
    /// <summary>Called when a new output entry is flushed/written by the session.</summary>
    void OnOutput(OutputEntry entry);

    /// <summary>Called when the prompt queue changes (add/remove/clear).</summary>
    void OnQueueChanged(List<string> queue);

    /// <summary>Called when session state changes (Idle ↔ Running).</summary>
    void OnStateChanged(SessionRunState state);
}