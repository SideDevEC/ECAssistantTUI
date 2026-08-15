# ECAssistant — Architecture

**Updated:** 2026-08-15
**Build:** 0 errors, 0 warnings
**Tests:** 940/940 passing

## Dependency Flow

```
Program.cs (Main → binder)
  │
  ├── creates EGuiConsole (GUI layer — full-screen TUI)
  │     └── Alternate screen buffer, fixed layout (output / status / input)
  │     └── Layer stack: SessionLayer (base) → HelpLayer → future layers
  │     └── Implements EGuiBase
  │     └── Knows NOTHING about session internals
  │
  ├── creates AgentSession (headless)
  │     └── EAgentEngine, Orchestrator, Tools, Memory, SubAgents
  │     └── All communicate via ISessionOutput (OutputState enums)
  │     └── ZERO references to EColor, EGuiBase, ANSI, Console
  │
  └── wires: session.AddListener(guiRenderer)
```

## Layers

### Program.cs (Binder)
- Entry point (`Main`)
- Creates GUI (`EGuiConsole`) in buffering mode, calls `InitConsole()` after startup messages are ready
- Wires sessions to GUI via `ConsoleUiRenderer`
- Main input loop: `> ` prompt, routes commands, ESC stops session
- Calls `ShutdownConsole()` on exit to restore terminal
- Session switch: clears screen, renders new session's full output history

### UI Layer (`UI/`)
- `EGuiConsole` — full-screen alternate-buffer TUI (like nano/vim)
  - Enter alternate screen buffer on init (`\x1b[?1049h`), exit on shutdown (`\x1b[?1049l`)
  - **Startup buffering:** output queued in `_startupBuffer` until `InitConsole()` flushes all at once — no blank gap on launch
  - Fixed layout: output region (rows 0 to height-3), status bar (row height-2), input line (row height-1)
  - Internal screen model: `_outputLines` stores ALL output (with ANSI codes), never lost
  - Dirty rendering: only repaints changed regions (`_outputDirty`, `_statusDirty`, `_inputDirty`, `_fullRepaint`)
  - Scrollback: ↑/↓ (1 line), PageUp/PageDown (full page), Home/End (top/bottom)
  - Status bar shows scroll position when scrolled up
  - Long lines wrapped (not truncated) to fit screen width
  - Terminal resize detection via 200ms background timer → immediate full repaint
  - Steady block cursor at input line (reverse-video space)
  - `ClearCanvas()` — wipes output buffer, resets scroll, repaints clean
  - Non-ANSI fallback: simple scroll-based output for dumb terminals
  - ANSI helpers: `StripAnsi()` (CSI + OSC parsing), `TruncateAnsi()`, `WrapLine()`
- **Layer stack:**
  - `IGuiLayer` interface: `OnActivate`, `OnResize`, `OnKey`
  - `SessionLayer` — base layer (layer 1), triggers session view repaint
  - `HelpLayer` — full-screen help, closes on Enter only
  - `PushLayer()` / `PopLayer()` — stack management, keys routed to active layer
  - Output still buffered while layer is active, restored on pop
  - `PaintLayerScreen()` — renders layer content centered with footer hint
- `EGuiBase` — abstract base for UI implementations
- `EColor` — ANSI color properties, used ONLY by ConsoleUiRenderer and EGuiConsole

### Session Layer (`Session/`)
- `AgentSession` — central hub, implements `ISessionOutput`
  - Each session: own engine, KV cache, tools, memory, output buffer, prompt queue, runner thread
  - Sessions share model weights (one GGUF in RAM), inference serialized via `SemaphoreSlim`
  - Output persisted to JSONL per session — switching back restores history
  - Prompt queue: FIFO, if running prompts queue for after current execution
  - `Stop()` only cancels the active session, not others
- `ISessionOutput` — the ONLY interface engine/tools use for output
- `IOutputListener` — UI implements this, gets notified by session
- `OutputState` enum: Info, Success, Warning, Error, Dim, Bold, Raw, System
- `OutputEntry` — JSONL record for persistent output buffer
- `ConsoleUiRenderer` — bridge between session and GUI (maps OutputState → ANSI colors → EGuiConsole)
  - `RenderHistory()` — renders full output history on session switch
- `LoadingIndicator` — animated loading dots in status bar (not \r animation)
- `SessionManager` — multi-session lifecycle, discovery, switching, status reports

### Engine Layer (`Engine/`)
- `EAgentEngine` — core LLM inference, context window, KV cache
- `AgentOrchestrator` — multi-step execution, tool dispatch
- `EDecisionLoop` — interactive clarifying questions
- `ParallelToolsExecutor` — dependency-ordered parallel tool execution
- `SubAgentManager` — isolated child agents with shared model weights
- `SecondaryModelLoader` — secondary LLM for task decomposition
- `TaskPlanner` — chained multi-step task planning
- All use `ISessionOutput` for output — no UI/color/Console references

### Tools Layer (`Tools/`)
- 10 tools: Shell, Git, CodeEditor, DotnetBuild, FileReader, WebSearch, WebFetch, FileResearch, BackgroundExec, SubAgent
- All headless — no `IColorFormatter`, no `EColor`, no `Console`
- Constructor-injected dependencies: `IProcessRunner`, `IFileSystem`, `IConfigProvider`, `IHttpClient`

### Services Layer (`Services/`)
- `Logger` — file-only, no console output
- `LlamaInferenceEngine` — LLamaSharp wrapper
- `InMemoryVectorStore` — FAISS alternative for vector memory
- `FileSystemAdapter`, `HttpClientAdapter`, `ConfigProvider` — service implementations

### Config Layer (`Config/`)
- `EAgentConfig` — strongly-typed app settings
- `ConfigLoader` — JSON deserialization with case-insensitive matching

### Interfaces (`Interfaces/`)
- `ITool`, `ILogger`, `IProcessRunner`, `IFileSystem`, `IHttpClient`, `IConfigProvider`

## Key Constraints
- `EColor` is used ONLY by `ConsoleUiRenderer`, `LoadingIndicator`, and `Program.cs`
- `Console.Write/WriteLine` appears ONLY in `EGuiConsole` (and `EColor` fallback)
- Engine, tools, memory, services have ZERO references to UI/color/Console
- All output stored in `EGuiConsole._outputLines` — persists across scrollback
- Alternate screen buffer preserves user's original terminal on exit
- Windows P/Invoke calls have `[SupportedOSPlatform("windows")]` — builds on Linux
- `InternalsVisibleTo` for test project — ANSI helpers and buffer logic testable