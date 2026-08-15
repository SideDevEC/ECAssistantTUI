# ECAssistant Session/UI Architecture Rewrite — Implementation Guide

**Created:** 2026-08-15
**Status:** In progress (stashed changes, broken build)
**Goal:** Clean session/UI separation with always-visible input field

## Git State
- Last good commit: `82aba21` on `main`
- Stashed: broken AgentSession.cs rewrite
- `git stash pop` to restore, or `git stash drop` and start fresh from `82aba21`

## New Files Already Created (in stash or committed)
- `Session/IOutputListener.cs` — listener interface (committed in stash)
- `Session/ISessionOutput.cs` — updated interface (committed in stash)

## Architecture (Agreed with the maintainer)

### Session is the central hub
- All downstream processing (engine, orchestrator, tools) communicates through session via `ISessionOutput`
- Session has: output buffer (JSON file), string buffer, state, listener list
- Session never knows what UI is attached — listeners behind `IOutputListener` interface

### IOutputListener Interface
```csharp
public interface IOutputListener
{
    void OnOutput(string text, OutputState state);  // flushed line
    void OnStreamStart();                            // streaming began
    void OnStreamStop();                             // streaming ended
}
```

### ISessionOutput Interface (engine/orchestrator/tools use this)
```csharp
public interface ISessionOutput
{
    void StartStream(OutputState state);   // empty buffer, set state, notify listeners
    void Write(string text);                // append to buffer ONLY — no output
    void StopStream();                      // notify listeners stream stopped
    void WriteLine(string text, OutputState state);  // output to JSON + notify listeners
    string GetStreamBuffer();               // returns current buffer content
    OutputState GetStreamState();           // returns current state
    void WriteInfo(string text);            // convenience
    void WriteSuccess(string text);        // convenience
    void WriteWarning(string text);         // convenience
    void WriteError(string text);           // convenience
    void WriteDim(string text);             // convenience
    void BlankLine();                       // convenience
}
```

### Session Methods — Exact Logic

**StartStream(state):**
1. Empty string buffer
2. Set internal state to `state`
3. Set `_streaming = true`
4. Notify all listeners: `OnStreamStart()`

**Write(text):**
1. Append text to string buffer
2. Nothing else — no JSON, no listeners, no output

**StopStream():**
1. Set `_streaming = false`
2. Notify all listeners: `OnStreamStop()`

**WriteLine(text, state):**
1. If `_streaming` is true, call `StopStream()` first
2. Create OutputEntry with text + state
3. Write entry to JSON file
4. Add entry to in-memory output buffer list
5. Iterate listeners, call `listener.OnOutput(text, state)`
6. Update internal state to `state`

**GetStreamBuffer():** Returns `_streamBuffer.ToString()` (thread-safe)

**GetStreamState():** Returns `_currentState` (thread-safe)

**Stop():** Cancels `_executionCts` (UI calls this on ESC)

### Usage Patterns

**Streaming (token by token):**
```csharp
_out.StartStream(OutputState.Raw);
await foreach (var token in executor.InferAsync(...))
    _out.Write(token);
_out.StopStream();
_out.WriteLine(_out.GetStreamBuffer(), OutputState.Raw);
```

**Non-streaming:**
```csharp
_out.WriteLine("message", OutputState.Info);
```

## What Needs To Be Done

### 1. Rewrite AgentSession.cs output methods
- Replace old `WriteRaw`, `WriteRawDirect`, `Write(text, state)`, `WriteLine`, `FlushBuffer`, `NotifyUi` with the new methods above
- Replace `IUiRenderer` with `IOutputListener` everywhere
- Replace `AttachUi`/`DetachUi` with `AddListener`/`RemoveListener` (already done in commit `82aba21`)
- Add `Stop()` method that cancels `_executionCts`
- Remove timer-based flush (`_streamFlushTimer`, `StartStreamFlushTimer`)
- Remove `FlushBuffer` (no longer needed — `WriteLine` handles output directly)
- Keep `WriteEntryToFile` for JSON output
- Keep `_outputBuffer` list (for UI history display)
- All methods must be thread-safe (use locks)

### 2. Update OutputTypes.cs
- Remove old `IUiRenderer` interface (replaced by `IOutputListener`)
- Remove `OnRawDirect`, `OnQueueChanged`, `OnStateChanged` from interface
- Keep `OutputEntry` class (for JSON file)
- Keep `OutputState` enum

### 3. Update EAgentEngine.cs
- Remove ALL `Program.Gui` references
- Remove `Program.Gui.IsEscapePressed()` — engine only checks `ExecutionToken.IsCancellationRequested`
- Replace `_out?.WriteRawDirect(token)` with `_out?.Write(token)`
- Before token stream: call `_out?.StartStream(OutputState.Raw)`
- After token stream: call `_out?.StopStream()` then `_out?.WriteLine(_out.GetStreamBuffer(), OutputState.Raw)`
- Replace `_out?.WriteRaw(token)` with `_out?.Write(token)`
- Keep `_out?.WriteLine(text, state)` as-is (already correct)
- Keep `_out?.WriteInfo/WriteWarning/WriteError/WriteDim` (they map to WriteLine)
- Replace `Program.Gui.LogInternal(...)` with `_out?.WriteLine(text, OutputState.Info)`

### 4. Update Orchestrator.cs
- Remove ALL `Program.Gui` references
- `Program.Gui.PromptRaw("[y/N]")` for approvals → needs a callback or ISessionOutput method
  - Option: Add `bool RequestApproval(string message)` to ISessionOutput that UI handles
  - Or: Engine decides approvals autonomously (no user prompt during execution)
  - **Ask the maintainer** how to handle approval prompts
- `Program.Gui.WriteLineColored(...)` → `_out?.WriteLine(text, state)`
- `EGuiBase.Truncate(...)` → keep as pure utility (it's stateless, allowed per policy)

### 5. Update EDecisionLoop.cs
- Remove ALL `Program.Gui` references
- `Program.Gui.BlankLine()` → `_out?.BlankLine()`
- `Program.Gui.WriteLineColored()` → `_out?.WriteLine(text, state)`
- `Program.Gui.WriteRaw()` → `_out?.Write(text)`
- `Program.Gui.PromptRaw("")` → needs callback or ISessionOutput method
  - Same approval prompt issue as Orchestrator

### 6. Update ParallelToolExecutor.cs
- Remove `Program.Gui.PromptRaw(...)` for approval prompts
- Same issue as above

### 7. Rewrite EGuiConsole.cs
- Input field always visible with `> ` prompt
- Main loop: poll for keypress, if Enter → queue to session, reprint `> `
- ESC key → call `session.Stop()` (session reference needed)
- During streaming: UI polls `session.GetStreamBuffer()` on a timer/thread to render live tokens
- `OnOutput(text, state)` → write to console above input line
- `OnStreamStart()` → start polling GetStreamBuffer
- `OnStreamStop()` → stop polling
- Output writes above input line (clear input, write output, reprint input + buffer)

### 8. Update ConsoleUiRenderer.cs
- Implement `IOutputListener` (not `IUiRenderer`)
- `OnOutput(text, state)` → write to EGuiConsole with color mapping
- `OnStreamStart()` → set streaming flag, start polling
- `OnStreamStop()` → stop polling
- Remove `RenderHistory` (UI reads JSON file directly when selecting session)
- Remove `OnQueueChanged`, `OnStateChanged` (removed from interface)

### 9. Update Program.cs
- Main loop: show `> `, read input, call `session.Prompt(input)`, show `> ` again
- ESC → `session.Stop()`
- Session switch: `RemoveListener(oldUi)`, `AddListener(newUi)`
- Remove `StartStreamFlushTimer` call
- UI needs reference to active session for `Stop()` and `GetStreamBuffer()`

## Open Questions for the maintainer
1. **Approval prompts** — `Program.Gui.PromptRaw("[y/N]")` in Orchestrator and ParallelToolExecutor. Options:
   - A: Add `RequestApproval(string message)` to ISessionOutput (UI shows prompt, returns bool)
   - B: Auto-approve all tool calls (no user prompt during execution)
   - C: Config-based approval (pre-approved tools skip prompt)
2. **GetStream(listener) method** — the maintainer mentioned this but we didn't finalize. Is it needed or is polling `GetStreamBuffer()` enough?

## Files Changed So Far
- `Session/IOutputListener.cs` — NEW, committed
- `Session/ISessionOutput.cs` — updated, committed  
- `Session/AgentSession.cs` — partially rewritten (BROKEN in stash)
- `Session/OutputTypes.cs` — needs IUiRenderer removed
- `Session/ConsoleUiRenderer.cs` — needs IOutputListener implementation
- `UI/EGuiConsole.cs` — needs full rewrite for polling + always-visible input
- `Engine/EAgentEngine.cs` — needs Program.Gui removed, StartStream/Write/StopStream
- `Orchestrator.cs` — needs Program.Gui removed
- `Engine/EDecisionLoop.cs` — needs Program.Gui removed
- `Engine/ParallelToolExecutor.cs` — needs Program.Gui removed
- `Program.cs` — needs main loop update, ESC → session.Stop()

## Build State
- `82aba21` builds clean (0 errors, 0 warnings)
- Stash has broken AgentSession.cs — drop stash and rewrite from `82aba21`

## Key Constraint
- **No processing logic touches UI.** Engine/orchestrator/tools only use `ISessionOutput`.
- Session buffers + notifies listeners. UI implements `IOutputListener`.
- `Program.Gui` must not appear in Engine/, Orchestrator.cs, Tools/, or Session/AgentSession.cs