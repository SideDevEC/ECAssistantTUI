using ECAssistant.Core.Memory;
using ECAssistant.Core.Session;

namespace ECAssistant.TUI.Controller;

/// <summary>
/// Interface for the application controller that binds the TUI to Core.
/// </summary>
public interface IAppController
{
    Task<int> RunAsync();
    ECAssistant.Core.Memory.VectorMemoryStore? VectorMemory { get; }
    ECAssistant.Core.Session.AgentSession? ActiveSession { get; }
}