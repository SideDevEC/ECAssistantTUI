# ECAssistant — Architecture

**Updated:** 2026-08-15 (v10.23)
**Build:** 0 errors, 0 warnings
**Tests:** 940/940 passing

## Project Structure (v10.23: Core + App Split)

```
ECAssistant.sln
├── ECAssistant.Core.csproj    ← Class library (DLL) — all engine, tools, session, memory, testing
│     OutputType=Library, AssemblyName=ECAssistant.Core
│     InternalsVisibleTo: e-assistant, ECAssistant.Tests
│     NuGet packages: LLamaSharp, LLamaSharp.Backend.Cpu/Vulkan/Cuda12
│
├── ECAssistant.csproj         ← Console exe — only Program.cs
│     OutputType=Exe, AssemblyName=e-assistant
│     ProjectReference → ECAssistant.Core.csproj
│
└── Tests/ECAssistant.Tests.csproj
      ProjectReference → ECAssistant.Core.csproj
```

## Dependency Flow

```
Program.cs (App — Main → binder)
  │
  ├── creates EGuiConsole (GUI layer — full-screen TUI)
  │     └── Alternate screen buffer, fixed layout (output / status / input)
  │     └── Layer stack: SessionLayer (base) → HelpLayer → future layers
  │     └── Implements EGuiBase
  │     └── Knows NOTHING about session internals
  │
  ├── creates SessionBuilder (Core — public API for library consumers)
  │     └── Configures: vector memory, project context, built-in tools, secondary model, sub-agents
  │     └── RegisterBuiltInTools flag — skip to use ONLY custom tools
  │     └── EnableVectorMemory / EnableSubAgents / EnableSecondaryModel overrides
  │
  ├── creates AgentSession (Core — headless)
  │     └── EAgentEngine, Orchestrator, Tools, Memory, SubAgents
  │     └── All communicate via ISessionOutput (OutputState enums)
  │     └── ZERO references to EColor, EGuiBase, ANSI, Console
  │
  └── wires: session.AddListener(guiRenderer)
```

## Library Integration (v10.23)

**ECAssistant.Core.dll** can be referenced by any .NET 8 project (e.g., ECSQL).

### Integration Points
- **`SessionBuilder`** — public class, replaces old `Program.InitSessionAsync`. Configures a session with standard tools.
- **`EGuiBase`** (abstract) — implement for custom UI (Avalonia, web, etc.)
- **`IOutputListener`** — implement to receive live output events from sessions
- **`EToolBase`** (abstract) — subclass for custom domain-specific tools
- **`AgentSession`** — central hub: create, register tools, attach listeners, call `Prompt()`
- **`EAgentEngine.SystemPromptPath`** / `SystemPromptText` — inject custom system prompt (file path or direct string)

### Usage Example (from ECSQL or any .NET 8 app)
```csharp
var config = ConfigLoader.Load("appsettings.json");
var sessionManager = new SessionManager(config, modelPath, workingDir, logger);
var session = sessionManager.Main;
var builder = new SessionBuilder(config, workingDir, userConfigDir, logger);
await builder.BuildAsync(session);                  // standard tools + memory
session.RegisterTool(new SqlQueryTool(dbConnection)); // custom tool
session.AddListener(new AvaloniaUiListener(mainWindow)); // custom UI
session.Prompt("Find all tables without indexes");
```

### SessionBuilder Options
- `RegisterBuiltInTools` (default true) — set false for ONLY custom tools
- `EnableVectorMemory` / `EnableSubAgents` / `EnableSecondaryModel` — override config defaults

## Layers

### Program.cs (App — Binder)
- Entry point (`Main`)
- Creates GUI (`EGuiConsole`) in buffering mode, calls `InitConsole()` after startup messages are ready
- Wires sessions to GUI via `ConsoleUiRenderer`
- Uses `SessionBuilder` (from Core) to initialize sessions
- Main input loop: `> ` prompt, routes commands, ESC stops session
- Calls `ShutdownConsole()` on exit to restore terminal

### UI Layer (`UI/`) — Core
- `EGuiConsole` — full-screen alternate-buffer TUI (like nano/vim)
- **Layer stack:** SessionLayer (base), HelpLayer — extensible via `IGuiLayer`
- `EGuiBase` — abstract base for UI implementations (swappable: console, GUI, web)
- `EColor` — ANSI color properties, used ONLY by ConsoleUiRenderer and EGuiConsole

### Session Layer (`Session/`) — Core
- `AgentSession` — central hub, implements `ISessionOutput`
  - Each session: own engine, KV cache, tools, memory, output buffer, prompt queue, runner thread
  - Sessions share model weights (one GGUF in RAM), inference serialized via `SemaphoreSlim`
- `SessionBuilder` — public API for initializing sessions with standard tools (v10.23)
- `ISessionOutput` — the ONLY interface engine/tools use for output
- `IOutputListener` — UI implements this, gets notified by session
- `SessionManager` — multi-session lifecycle, discovery, switching, status reports

### Engine Layer (`Engine/`) — Core
- `EAgentEngine` — core LLM inference, context window, KV cache
  - `SystemPromptPath` / `SystemPromptText` properties for injectable prompts (v10.23)
- `AgentOrchestrator` — multi-step execution, tool dispatch
- `EDecisionLoop` — interactive clarifying questions
- `ParallelToolsExecutor` — dependency-ordered parallel tool execution
- `SubAgentManager` — isolated child agents with shared model weights
- `SecondaryModelLoader` — secondary LLM for task decomposition
- `TaskPlanner` — chained multi-step task planning
- All use `ISessionOutput` for output — no UI/color/Console references

### Tools Layer (`Tools/`) — Core
- 10 tools: Shell, Git, CodeEditor, DotnetBuild, FileReader, WebSearch, WebFetch, FileResearch, BackgroundExec, SubAgent
- All headless — no `IColorFormatter`, no `EColor`, no `Console`
- Constructor-injected dependencies: `IProcessRunner`, `IFileSystem`, `IConfigProvider`, `IHttpClient`
- Subclass `EToolBase` to add custom tools — no core changes needed

### Services Layer (`Services/`) — Core
- `Logger` — file-only, no console output
- `LlamaInferenceEngine` — LLamaSharp wrapper
- `InMemoryVectorStore` — FAISS alternative for vector memory
- `FileSystemAdapter`, `HttpClientAdapter`, `ConfigProvider` — service implementations

### Config Layer (`Config/`) — Core
- `EAgentConfig` — strongly-typed app settings
- `ConfigLoader` — JSON deserialization with case-insensitive matching

### Interfaces (`Interfaces/`) — Core
- `ITool`, `ILogger`, `IProcessRunner`, `IFileSystem`, `IHttpClient`, `IConfigProvider`
- `IEngine`, `IInferenceEngine`, `IModelLoader`, `IOutputRenderer`, `ITerminal`
- `IMemoryService`, `IVectorEmbedder`, `IVectorStore`, `IContextManager`

### Testing (`Testing/`) — Core
- `TestRunner` — automated test execution (uses `TestRunner.TestGui` static, decoupled from `Program`)
- `MockEngine` — model-independent test engine (no GGUF needed)
- `EGuiTestHarness` — non-interactive GUI for testing
- `EcaTests` — test scenario definitions

## Key Constraints
- `EColor` is used ONLY by `ConsoleUiRenderer`, `LoadingIndicator`, and `Program.cs`
- `Console.Write/WriteLine` appears ONLY in `EGuiConsole` (and `EColor` fallback)
- Engine, tools, memory, services have ZERO references to UI/color/Console
- `InternalsVisibleTo`: Core → `e-assistant` (App) + `ECAssistant.Tests`
- `Program.cs` is the ONLY file in the App project — everything else is in Core
- `SessionBuilder` is the public API for library consumers — no need to touch `Program.cs`