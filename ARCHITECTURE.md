# ECAssistant — Architecture

**Updated:** 2026-08-16 (v10.24.2)
**Build:** 0 errors, 0 warnings
**Tests:** 907/907 passing

## Project Structure

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
  │     └── Layer stack: SessionLayer (base) → HelpLayer
  │     └── Implements EGuiBase
  │
  ├── creates AgentConfigBuilder → Build() → EAgentConfig
  │     └── JSON-first: generates/loads appsettings.json in eca-data/
  │
  ├── creates SessionBuilder (Core — public API for library consumers)
  │     └── Configures: vector memory, project context, built-in tools, secondary model, sub-agents
  │     └── RegisterBuiltInTools flag — skip to use ONLY custom tools
  │
  ├── creates AgentSession (Core — headless)
  │     └── EAgentEngine, Orchestrator, Tools, Memory, SubAgents
  │     └── All communicate via ISessionOutput (OutputState enums)
  │     └── ZERO references to EColor, EGuiBase, ANSI, Console
  │
  └── wires: session.AddListener(guiRenderer)
```

## Library Integration

**ECAssistant.Core.dll** can be referenced by any .NET 8 project (e.g., ECSQL).

### Integration Points
- **`AgentConfigBuilder`** — fluent config builder, generates/loads `appsettings.json`
- **`SystemPromptBuilder`** — required `<lm>` tag rules + auto-detect OS + domain context
- **`SessionBuilder`** — initializes sessions with standard tools
- **`EGuiBase`** (abstract) — implement for custom UI (Avalonia, web, etc.)
- **`IOutputListener`** — implement to receive live output events from sessions
- **`EToolBase`** (abstract) — subclass for custom domain-specific tools
- **`AgentSession`** — central hub: create, register tools, attach listeners, call `Prompt()`
- **`EAgentEngine.SystemPromptPath`** / `SystemPromptText`** — inject custom system prompt

### Config Flow (JSON is source of truth)

```
AgentConfigBuilder.Build()
  │
  ├── 1. Resolve working dir: given path + "eca-data" (default: ./eca-data/)
  │
  ├── 2. appsettings.json exists there?
  │     ├── YES → load it, return it (code values IGNORED)
  │     └── NO  → generate it with code values + defaults, return it
  │
  └── Result: EAgentConfig
```

### Tool Registration Flow (v10.24)

```
session.RegisterTool(new SqlQueryTool(db, config))
  │
  ├── tool.Name = "SqlQuery"
  ├── config.Tools.ContainsKey("SqlQuery")?
  │     ├── YES → tool reads existing config from JSON
  │     └── NO  → call tool.GetConfigSection()
  │               → add to config.Tools["SqlQuery"]
  │               → AgentConfigBuilder.Update(config)  // persist to appsettings.json
  ├── tool.IsEnabled checked → skip if false
  └── tool registered on engine
```

### On-disk layout (library consumer)
```
./eca-data/
├── appsettings.json          ← generated on first run, editable by end users
│   ├── tools:
│   │   ├── EShellAgent: { enabled: true, use_pwsh_core: true, ... }
│   │   ├── EFileResearchTool: { enabled: true, default_extensions: [...], ... }
│   │   └── SqlQuery: { enabled: true, max_rows: 1000, ... }
│   └── ...
├── .sessions/main/
│   ├── transcript.json
│   └── ui_output.jsonl
├── Memory/
└── vecmem/
```

### Usage Example (from ECSQL or any .NET 8 app)
```csharp
// 1. Build config — generates appsettings.json on first run, loads it after
var config = AgentConfigBuilder.Create()
    .WithModel("/path/to/model.gguf")
    .ContextSize(16384)
    .GpuLayers(15)
    .Build();

// 2. Create session manager — loads GGUF into RAM
var workingDir = config.AgentSettings.WorkingDirectory;
var sessionManager = new SessionManager(config, config.Llm.ModelPath, workingDir, logger);
var session = sessionManager.Main;

// 3. Build system prompt with required <lm> tag rules + your domain
session.Engine.SystemPromptText = SystemPromptBuilder.Create()
    .WithAgentName("ECSQL Assistant")
    .WithDescription("You help users manage SQL databases.")
    .WithCustomRules("Always explain SQL before executing.")
    .Build();

// 4. Initialize with standard tools
var builder = new SessionBuilder(config, workingDir, workingDir, logger);
await builder.BuildAsync(session);

// 5. Add custom tools
session.RegisterTool(new SqlQueryTool(dbConnection, config));

// 6. Attach UI listener
session.AddListener(new AvaloniaUiListener(mainWindow));

// 7. Send prompts
session.Prompt("Find all tables without indexes");
```

### `<lm>` Tag System (in DLL, not overridable)
- `EAgentEngine.ExtractCleanResponse()` — parses `<lm>` containers
- `AgentOrchestrator` — detects `<toolcall>`, `<output>`, `<thinking>` tags, parses args to `Dictionary<string, string?>`
- `SystemPromptBuilder` — includes required tag rules in every prompt

### No Disk Dependencies
- `SubAgentManager` receives config via constructor injection — no `~/ECAssistant/` reads
- `ConfigProvider` has an `EAgentConfig` constructor — no file I/O for library consumers
- `EAgentConfig.RootPath` defaults to `"."` (not `"ECAssistant"`)
- System prompts injectable via `SystemPromptBuilder` or `EAgentEngine.SystemPromptText`

## Tool System (v10.24: Unified on EToolBase)

### EToolBase (abstract — all tools extend this)
```csharp
public abstract class EToolBase
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string UsageExample { get; }
    public virtual bool IsEnabled { get; protected set; } = true;
    
    // Returns default config section — every tool has at minimum { enabled = true }
    public virtual object GetConfigSection() => new { enabled = true };
    
    // Execute with pre-parsed dictionary arguments
    public abstract Task<EToolResult> ExecuteAsync(
        Dictionary<string, string?> arguments,
        CancellationToken cancellationToken = default);
    
    // Config helpers
    protected static T ReadCfg<T>(JsonElement? section, string key, T defaultValue);
    protected static bool IsToolEnabled(Dictionary<string, JsonElement> tools, string toolName);
}
```

### EToolResult
```csharp
public record EToolResult(bool Succeeded, string ToolName, string Output, string Error)
{
    public static EToolResult Success(string name, string output);
    public static EToolResult Failure(string name, string error);
}
```

### Built-in Tools (10)
| Tool | Name | Config Section |
|------|------|----------------|
| EShellAgent | EShellAgent | enabled, use_pwsh_core, fallback_to_powershell_exe, max_output_chars |
| EBackgroundExecTool | EBackgroundExec | enabled, workingDir |
| EWebSearchTool | EWebSearch | enabled |
| EDotnetBuildTool | DotnetBuild | enabled |
| EGitTool | EGitTool | enabled, workingDir |
| ECodeEditorTool | ECodeEditor | enabled |
| EFileReaderTool | EFileReader | enabled |
| EFileResearchTool | EFileResearchTool | enabled, default_extensions, max_chars_per_file, max_files_to_scan, query_limit |
| EWebFetchTool | EWebFetch | enabled |
| ESubAgentTool | ESubAgent | enabled |

### Custom Tools
Subclass `EToolBase`, override `Name`, `Description`, `UsageExample`, `ExecuteAsync`, `GetConfigSection()`:
```csharp
public class SqlQueryTool : EToolBase
{
    public override string Name => "SqlQuery";
    public override object GetConfigSection() => new { enabled = true, max_rows = 1000, timeout_seconds = 30 };
    public override async Task<EToolResult> ExecuteAsync(
        Dictionary<string, string?> args, CancellationToken ct = default)
    {
        var query = args["query"];
        // ... execute
        return EToolResult.Success("SqlQuery", results);
    }
}
```

### ITool Interface — DELETED (v10.24)
- All 9 built-in tools migrated from `ITool` to `EToolBase`
- `ToolAdapter` deleted — no longer needed
- `RegisterTool(ITool)` removed from engine and session
- All tools use `Dictionary<string, string?>` args (orchestrator parses XML tags)
- All tools return `EToolResult` (not raw string)

## Layers

### Program.cs (App — Binder)
- Entry point (`Main`)
- Creates GUI, loads config via `AgentConfigBuilder`, wires sessions
- Uses `SessionBuilder` to initialize sessions

### UI Layer (`UI/`) — Core
- `EGuiConsole` — full-screen alternate-buffer TUI
- `EGuiBase` — abstract base for UI implementations
- `EColor` — ANSI colors (UI layer only)

### Session Layer (`Session/`) — Core
- `AgentSession` — central hub, implements `ISessionOutput`
  - Auto-registers tool config sections on `RegisterTool()`
  - Passes `EAgentConfig` to orchestrator → SubAgentManager
- `SessionBuilder` — public API for session initialization
- `SessionManager` — multi-session lifecycle

### Engine Layer (`Engine/`) — Core
- `EAgentEngine` — LLM inference, context window, KV cache
- `AgentOrchestrator` — multi-step execution, tool dispatch, parses `<toolcall>` to Dictionary
- `SubAgentManager` — child agents, config injected
- `ParallelToolsExecutor` — dependency-ordered parallel tool execution
- `SecondaryModelLoader` — secondary LLM

### Config Layer (`Config/`) — Core
- `EAgentConfig` — `Tools` is `Dictionary<string, JsonElement>` (dynamic)
- `AgentConfigBuilder` — fluent, JSON-first, `Update()` for persistence
- `ConfigLoader` — JSON deserialization

### System Prompt (`SystemPromptBuilder.cs`) — Core
- Auto-detects OS (macOS/Windows/Linux)
- Always includes `<lm>` tag format rules
- Domain context via `WithAgentName()`, `WithDescription()`, `WithCustomRules()`

### Tools Layer (`Tools/`) — Core
- 10 built-in tools, all extend `EToolBase`
- `EToolBase` — abstract base with `GetConfigSection()`, `IsEnabled`, `ReadCfg<T>()`
- `EToolResult` — Success/Failure with Output/Error
- Subclass `EToolBase` to add custom tools

### Services Layer (`Services/`) — Core
- `Logger`, `LlamaInferenceEngine`, `InMemoryVectorStore`
- `ConfigProvider` — supports both file-based and preloaded config

### Testing (`Testing/`) — Core
- `TestRunner`, `MockEngine`, `EGuiTestHarness`

## Key Constraints
- `EColor` used ONLY by `ConsoleUiRenderer`, `LoadingIndicator`, `Program.cs`
- `Console.Write/WriteLine` ONLY in `EGuiConsole`
- Engine, tools, memory, services: ZERO references to UI/color/Console
- `Program.cs` is the ONLY file in the App project
- No hardcoded `~/ECAssistant/` paths in Core
- `<lm>` tag format rules always included via `SystemPromptBuilder`
- `AgentConfigBuilder`: JSON is source of truth
- All tools extend `EToolBase` — no `ITool` interface (v10.24)
- Tool config auto-registered on `RegisterTool()` if missing from JSON