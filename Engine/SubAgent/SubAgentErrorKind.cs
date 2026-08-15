using System.Text;

namespace ECAssistant.Engine;

public enum SubAgentErrorKind
{
    None,
    Timeout,
    TurnsExhausted,
    ToolFailure,
    ResourceLimitExceeded,
    CancelledByMainAgent,
    Exception,
    MaxRetriesExceeded
}