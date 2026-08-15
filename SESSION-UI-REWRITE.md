# ECAssistant Session/UI Architecture Rewrite — Implementation Guide

**Created:** 2026-08-15
**Status:** ✅ Complete (build clean, 909/917 tests pass — 8 pre-existing failures)
**Goal:** Clean session/UI separation with always-visible input field

## Git State
- Based on commit: `82aba21` (last good build)
- Broken stash dropped — rewrite done from clean commit

## Architecture (Implemented)

### Session is the central hub
- All downstream processing (engine, orchestrator, tools) communicates through session via `ISessionOutput`
- Session has: stream buffer, output buffer (JSONL file), listeners, state
- Session never knows what UI is attached — listeners behind `IOutputListener` interface

### IOutputListener Interface (Session/IOutputListener.cs)
```csharp
public interface IOutputListener
{
    void OnOutput(string text, OutputState state);   // flushed line
    void OnStreamStart();                             // streaming began
    void OnStreamStop();                              // streaming ended
    bool OnRequestApproval(string message);           // user approval — blocks until response
}
```

### ISessionOutput Interface (Session/ISessionOutput.cs)
```csharp
public interface ISessionOutput
{
    // Streaming
    void StartStream(OutputState state);
    void Write(string token);
    void StopStream();

    // Discrete output
    void WriteLine(string text, OutputState state = OutputState.Info);
    void BlankLine();

    // Convenience
    void WriteInfo(string text);
    void WriteSuccess(string text);
    void WriteWarning(string text);
    void WriteError(string text);
    void WriteDim(string text);

    // Stream buffer access
    string GetStreamBuffer();
    OutputState GetStreamState();

    // User interaction
    bool RequestApproval(string message);  // blocks until listener responds
}
```

### Approval Flow
- Engine/Orchestrator/ParallelToolExecutor calls `_out.RequestApproval(message)`
- Session writes the message as output, then asks the first listener
- If no listener attached: **blocks and waits** until one attaches (for background sessions)
- Listener's `OnRequestApproval` returns `true`/`false`
- Cancellation token is checked during the wait loop

### Streaming Pattern (engine token loop)
```csharp
_out.StartStream(OutputState.Raw);
await foreach (var token in executor.InferAsync(...))
    _out.Write(token);
_out.StopStream();
// buffer flushed via WriteLine on next discrete output
```

## Files Changed

### Interfaces (updated)
- `Session/ISessionOutput.cs` — new StartStream/Write/StopStream/GetStreamBuffer/GetStreamState/RequestApproval
- `Session/IOutputListener.cs` — new OnStreamStart/OnStreamStop/OnRequestApproval
- `Session/OutputTypes.cs` — removed old IUiRenderer, kept OutputEntry + OutputState

### Core rewrite
- `Session/AgentSession.cs` — full rewrite: new streaming methods, RequestApproval (waits for listener), removed timer/FlushBuffer, Stop() cancels CTS
- `Session/ConsoleUiRenderer.cs` — implements IOutputListener, removed IUiRenderer

### Engine (Program.Gui removed)
- `Engine/EAgentEngine.cs` — 4x Program.Gui.LogInternal → _logger, IsEscapePressed → CTS only, StartStream/Write/StopStream pattern for tokens
- `Orchestrator.cs` — Program.Gui.PromptRaw → _out.RequestApproval, Program.Gui.WriteLineColored → _logger.Debug
- `Engine/EDecisionLoop.cs` — all Program.Gui → _out methods, constructor takes ISessionOutput
- `Engine/ParallelToolExecutor.cs` — Program.Gui.PromptRaw → _out.RequestApproval, constructor takes ISessionOutput

### Program/UI
- `Program.cs` — removed StartStreamFlushTimer call, Program.Gui.WriteLineColored → _color.TagBold
- `UI/EGuiConsole.cs` — unchanged (already had always-visible input)
- `UI/EGuiBase.cs` — unchanged

### Testing
- `Testing/MockEngine.cs` — WriteRaw → StartStream/Write/StopStream/WriteLine
- `Testing/TestSessionOutput.cs` — NEW: test ISessionOutput that routes approvals to EGuiTestHarness
- `Tests/Session/ConsoleUiRendererTests.cs` — rewritten for IOutputListener interface
- `Tests/Integration/ToolPipelineIntegrationTests.cs` — uses TestSessionOutput
- `Tests/Integration/OrchestratorIntegrationTests.cs` — uses TestSessionOutput
- `Tests/Integration/ParallelToolExecutorIntegrationTests.cs` — uses TestSessionOutput
- `Tests/Integration/SubAgentIntegrationTests.cs` — uses TestSessionOutput

## Build & Test Status
- **Build:** 0 errors, 0 warnings ✅
- **Tests:** 909 passed, 8 failed (all pre-existing — EDotnetBuildTool/ToolAdapter string matching)

## Key Constraint Met
- **No `Program.Gui` in Engine/, Orchestrator.cs, Tools/, or Session/AgentSession.cs** ✅
- All downstream components use `ISessionOutput` exclusively
- Session is the approval hub — blocks until listener responds

## Open Items (not blocking)
- EGuiConsole polling of GetStreamBuffer during streaming (OnStreamStart → poll, OnStreamStop → stop) — currently UI writes output on OnOutput, streaming is buffered. Can add live polling later.
- 8 pre-existing test failures (EDotnetBuildTool + ToolAdapter) — unrelated to this refactor