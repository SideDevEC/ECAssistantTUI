namespace ECAssistant.Session;

/// <summary>
/// Interface for session output — implemented by AgentSession.
///
/// This is the contract that all components (orchestrator, engine, tools)
/// use for output and user interaction. They receive an ISessionOutput
/// reference and call Write/WriteLine/StartStream/RequestApproval on it.
/// They don't know about the session itself, the JSONL file, or the UI
/// attachment — just that they can write output and request approval.
///
/// This breaks the circular dependency: Session references Orchestrator/Engine,
/// but Orchestrator/Engine only reference ISessionOutput (the interface).
/// </summary>
public interface ISessionOutput
{
    // ── Streaming (token-by-token) ──

    /// <summary>Begin a stream: empty buffer, set state, notify listeners.</summary>
    void StartStream(OutputState state);

    /// <summary>Append a token to the stream buffer. No file I/O, no listener notification.</summary>
    void Write(string token);

    /// <summary>End the stream: notify listeners that streaming stopped.</summary>
    void StopStream();

    // ── Discrete output (lines) ──

    /// <summary>Write a line with a state. Stops any active stream first.</summary>
    void WriteLine(string text, OutputState state = OutputState.Info);

    /// <summary>Write a blank line.</summary>
    void BlankLine();

    // ── Convenience methods ──

    /// <summary>Write an info message.</summary>
    void WriteInfo(string text);

    /// <summary>Write a success message.</summary>
    void WriteSuccess(string text);

    /// <summary>Write a warning.</summary>
    void WriteWarning(string text);

    /// <summary>Write an error.</summary>
    void WriteError(string text);

    /// <summary>Write dim text.</summary>
    void WriteDim(string text);

    /// <summary>Write a tagged line: [TAG] message with a state.</summary>
    void WriteTag(string tag, string message, OutputState state = OutputState.Info);

    // ── Stream buffer access ──

    /// <summary>Get the current stream buffer content (thread-safe).</summary>
    string GetStreamBuffer();

    /// <summary>Get the current stream state (thread-safe).</summary>
    OutputState GetStreamState();

    // ── User interaction ──

    /// <summary>
    /// Request user approval. Blocks until the attached listener responds.
    /// If no listener is attached, blocks until one attaches and responds.
    /// Returns true if approved, false if denied.
    /// </summary>
    bool RequestApproval(string message);
}