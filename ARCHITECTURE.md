# ECAssistant — Architecture

**Updated:** 2026-08-15
**Build:** 0 errors, 0 warnings
**Tests:** 892/892 passing

## Dependency Flow

```
Program.cs (Main → binder)
  │
  ├── creates EGuiConsole (GUI layer — full-screen TUI)
  │     └── Alternate screen buffer, fixed layout (output / status / input)
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
- Creates GUI (`EGuiConsole`) and session (`AgentSession`)
- Wires them together (`session.AddListener(renderer)`)
- Main input loop: `> ` prompt, routes commands, ESC stops session
- Calls `InitConsole()` on startup, `ShutdownConsole()` on exit
- Uses `EColor` directly for its own startup messages (it's in the UI layer)

### UI Layer (`UI/`)
- `EGuiConsole` — full-screen alternate-buffer TUI (like nano/vim)
  - Enter alternate screen buffer on init (`\x1b[?1049h`), exit on shutdown (`\x1b[?1049l`)
  - Fixed layout: output region (rows 0 to height-3), status bar (row height-2), input line (row height-1)
  - Internal screen model: `_outputLines` stores ALL output (with ANSI codes), never lost
  - Dirty rendering: only repaints changed regions (`_outputDirty`, `_statusDirty`, `_inputDirty`, `_fullRepaint`)
  - Scrollback: ↑/↓ (1 line), PageUp/PageDown (full page), Home/End (top/bottom)
  - Status bar shows scroll position when scrolled up
  - Long lines wrapped (not truncated) to fit screen width
  - Terminal resize detection via `CheckResize()` → full repaint
  - `ClearCanvas()` — wipes output buffer, resets scroll, repaints clean
  - Non-ANSI fallback: simple scroll-based output for dumb terminals
  - `EColor` — ANSI color properties, used ONLY by ConsoleUiRenderer and EGuiConsole
  - `EGuiBase` — abstract base for UI implementations
  - Maps `OutputState` → ANSI colors
  - Only class that touches the terminal (Console.Write)

### Session Layer (`Session/`)
- `AgentSession` — central hub, implements `ISessionOutput`
- `ISessionOutput` — the ONLY interface engine/tools use for output
- `IOutputListener` — UI implements this, gets notified by session
- `OutputState` enum: Info, Success, Warning, Error, Dim, Bold, Raw, System
- `OutputEntry` — JSONL record for persistent output buffer
- `ConsoleUiRenderer` — bridge between session and GUI (maps OutputState → ANSI colors → EGuiConsole)
- `LoadingIndicator` — animated loading dots (uses EColor)
- `SessionManager` — multi-session lifecycle
- `SessionDiscovery` — finds existing sessions on disk

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
- Output goes through orchestrator → `ISessionOutput`

### Services Layer (`Services/`)
- `Logger` — file-only, no console output
- `LlamaInferenceEngine` — LLamaSharp wrapper
- `InMemoryVectorStore` — FAISS alternative for vector memory
- `FileSystemAdapter` — IFileSystem implementation
- `HttpClientAdapter` — IHttpClient implementation
- `ConfigProvider` — IConfigProvider implementation

### Config Layer (`Config/`)
- `EAgentConfig` — strongly-typed app settings
- `ConfigLoader` — JSON deserialization with case-insensitive matching
- All config models: LlmConfig, MemoryConfig, SubAgentConfig, etc.

### Interfaces (`Interfaces/`)
- `ITool` — tool interface
- `ILogger` — logging interface (file-only, no GUI)
- `IProcessRunner`, `IFileSystem`, `IHttpClient`, `IConfigProvider` — service abstractions
- `ITerminal` — terminal abstraction (unused, candidate for removal)

## Key Constraints
- `EColor` is used ONLY by `ConsoleUiRenderer`, `LoadingIndicator`, and `Program.cs`
- `Console.Write/WriteLine` appears ONLY in `EGuiConsole` (and `EColor` fallback)
- Engine, tools, memory, services have ZERO references to UI/color/Console
- All output stored in `EGuiConsole._outputLines` — persists across scrollback
- Alternate screen buffer preserves user's original terminal on exit