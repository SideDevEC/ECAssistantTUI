namespace ECAssistant.Session;

/// <summary>
/// Interface for session output — implemented by AgentSession.
///
/// This is the contract that all components (orchestrator, engine, tools)
/// use for output. They receive an ISessionOutput reference and call
/// Write/WriteLine/WriteRaw on it. They don't know about the session itself,
/// the JSONL file, or the UI attachment — just that they can write output.
///
/// This breaks the circular dependency: Session references Orchestrator/Engine,
/// but Orchestrator/Engine only reference ISessionOutput (the interface).
/// </summary>
public interface ISessionOutput
{
    /// <summary>Append a raw token to the stream buffer (no flush, no file I/O).</summary>
    void WriteRaw(string token);

    /// <summary>Write raw text directly to output — bypasses cursor tracking, for token streaming.</summary>
    void WriteRawDirect(string token);

    /// <summary>Write text with a state. If state changes, flush buffer first.</summary>
    void Write(string text, OutputState state);

    /// <summary>Write a line with a state. Flushes buffer first if not empty.</summary>
    void WriteLine(string text, OutputState state = OutputState.Info);

    /// <summary>Write a blank line.</summary>
    void BlankLine();

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
}