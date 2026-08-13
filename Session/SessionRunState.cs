namespace ECAssistant.Session;

/// <summary>
/// Session run state — whether the session is idle or executing.
/// </summary>
public enum SessionRunState
{
    /// <summary>No execution running, waiting for input</summary>
    Idle,
    /// <summary>Orchestrator is executing, prompt queue may have items</summary>
    Running,
    /// <summary>Session has been stopped and is shutting down</summary>
    Stopping
}