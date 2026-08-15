namespace ECAssistant.Session;

/// <summary>
/// Listener interface for session output.
/// Any UI (console, web, test harness) implements this.
/// Session never knows what's behind it.
/// </summary>
public interface IOutputListener
{
    /// <summary>Called when a line is written (non-stream output or flushed stream content).</summary>
    void OnOutput(string text, OutputState state);

    /// <summary>Called when streaming starts. Listener can poll GetStreamBuffer() for live content.</summary>
    void OnStreamStart();

    /// <summary>Called when streaming stops. Buffer is about to be flushed via WriteLine.</summary>
    void OnStreamStop();

    /// <summary>
    /// Called when the session needs user approval. The listener must display the
    /// message to the user and collect a yes/no response. Blocks until the user responds.
    /// Returns true if approved, false if denied.
    /// </summary>
    bool OnRequestApproval(string message);
}