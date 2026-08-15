# ECAssistant — Architecture

**Updated:** 2026-08-15
**Build:** 0 errors, 0 warnings
**Tests:** 892/892 passing

## Dependency Flow

```
Program.cs (Main → binder)
  │
  ├── creates EGuiConsole (GUI layer)
  │     └── EColor, ANSI codes, Console I/O
  │     └── Implements IOutputListener
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
- Uses `EColor` directly for its own startup messages (it's in the UI layer)

### Session Layer (`Session/`)
- `AgentSession` — central hub, implements `ISessionOutput`
- `ISessionOutput` — the ONLY interface engine/tools use for output
- `IOutputListener` — UI implements this, gets notified by session
- `OutputState` enum: Info, Success, Warning, Error, Dim, Bold, Raw, System
- `OutputEntry` — JSONL record for persistent output buffer
- `ConsoleUiRenderer` — bridge between session and GUI (uses EColor)
- `LoadingIndicator` — animated loading dots (uses EColor)
- `SessionManager` — multi-session lifecycle
- `SessionDiscovery` — finds existing sessions on disk

### Engine Layer (`Engine/`)
- `EAgentEngine` — core LLM inference, context window, KV cache
- `AgentOrchestrator` — multi-step execution, tool dispatch
- `EDecisionLoop` — interactive clarifying questions
- `ParallelToolExecutor` — dependency-ordered parallel tool execution
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

### UI Layer (`UI/`)
- `EGuiConsole` — the ONLY class that touches the terminal
- `EGuiBase` — abstract base for UI implementations
- Maps `OutputState` → ANSI colors
- Always-visible `> ` prompt, output scrolls above

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
- `Console.Write/WriteLine` appears ONLY in `EGuiConsole`, `EColor` (fallback), and `TerminalAdapter` (unused)
- `EGuiBase.Truncate` kept for UI/ compatibility; engine uses `StringUtil.Truncate`
- `IColorFormatter` and `ColorFormatter` deleted — no longer needed