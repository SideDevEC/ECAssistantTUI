# ECAssistant Architecture (v11.1 — 2026-08-15)

**Summary:** Strict OOP refactor complete. All mutable statics eliminated, full interface-based design, constructor injection, 912 tests passing.

## OOP Principles (IDENTITY.md)

- No mutable static state — statics discouraged but permitted for pure/stateless functions
- Constructor injection — all dependencies passed via constructor
- Program to interfaces — depend on abstractions, not implementations
- One type per file — each class/interface in its own file
- Encapsulation — private/protected fields, no public mutable state
- Single responsibility — one purpose per class
- Cross-dependency free — dependencies flow one direction

## Build & Test Status

- **Build:** 0 errors, 0 warnings (main + test projects)
- **Tests:** 912 total (825 unit + 87 integration), all passing
- **Test files:** 59 (50 unit + 9 integration)
- **App launch:** Verified — loads config, model, tools, sessions, KV cache

## Dependency Flow

```
┌─────────────┐     ┌──────────────┐     ┌──────────────┐
│   UI Layer   │────>│  Engine Layer│────>│  Tool Layer  │
│  (Terminal)  │     │  (Orchestration)│  │ (Execution)  │
└─────────────┘     └──────────────┘     └──────────────┘
                           │                      │
                           v                      v
                    ┌──────────────┐     ┌──────────────┐
                    │ Service Layer │     │  IO Layer    │
                    │ (Memory,      │     │ (File, Web,   │
                    │  Config,      │     │  Shell)      │
                    │  Inference)   │     └──────────────┘
                    └──────────────┘
```

Dependencies flow: UI → Engine → Service/Tool → IO

## Module Boundaries

### 1. Interfaces (Interfaces/)
- `ITool` — unified tool interface (Name, Description, ExecuteAsync, GetPolicy)
- `IInferenceEngine` — LLM inference abstraction
- `IMemoryService` — persistent memory with vector search
- `IConfigProvider` — configuration access abstraction
- `IContextManager` — context window management
- `IFileSystem` — file system abstraction (ReadFile, WriteFile, FileExists, ListFiles)
- `IProcessRunner` — process execution abstraction
- `IHttpClient` — HTTP client abstraction
- `IColorFormatter` — ANSI color formatting
- `ILogger` — logging abstraction (Debug, Info, Warn, Error)
- `IVectorStore` — vector similarity search
- `IModelLoader` — GGUF model loading
- `IOutputRenderer` — terminal output
- `ITerminal` — terminal I/O
- `IToolPolicyEvaluator` — tool permission evaluation
- `IVectorEmbedder` — text embedding
- `IEngine` — engine interface

### 2. Engine Layer (Engine/)
- `EAgentEngine` — main agent orchestration, conversation flow, tool dispatch
- `AgentOrchestrator` — decision loop, multi-step execution, tool call parsing
- `ContextWindow` — context budget tracking with auto-summarize
- `ConversationTranscript` — message history persistence
- `ParallelToolExecutor` — batch tool execution with dependency analysis
- `ToolDependencyAnalyzer` — analyzes tool call dependencies for parallel grouping
- `TaskPlanner` — decomposes requests into steps
- `StepMapper` — maps execution plan to tool calls
- `SelfCorrectionManager` — failure tracking and recovery
- `EDecisionLoop` — decision loop state machine
- `ProjectContextManager` — project file scanning and context
- `SubAgentManager` — spawns and manages sub-agent sessions
- `TokenCounter` — token counting via LLamaSharp tokenizer
- `SecondaryModelLoader` — secondary model for summaries

### 3. Tool Layer (Tools/)
- `EToolBase` — abstract base class for tools
- `ToolAdapter` — wraps ITool as EToolBase for engine compatibility
- `ToolPolicy` — permission manager (Allow, ApprovalRequired, Blocked)
- `EShellAgent` — shell command execution
- `EBackgroundExecTool` — background process management
- `EWebSearchTool` — web search
- `EWebFetchTool` — URL fetching and HTML-to-text
- `EDotnetBuildTool` — .NET build/test
- `EGitTool` — git operations
- `ECodeEditorTool` — file patching with diff
- `EFileReaderTool` — controlled file reading
- `EFileResearchTool` — multi-file search
- `EFileAnalyzer` — file analysis (example tool)
- `ESubAgentTool` — sub-agent spawning

### 4. Service Layer (Services/)
- `LlamaInferenceEngine` — LLamaSharp inference (StatelessExecutor + InferAsync)
- `Logger` — file + console logging implementing ILogger
- `FileSystemAdapter` — real file system implementing IFileSystem
- `ProcessRunner` — real process execution implementing IProcessRunner
- `HttpClientAdapter` — HTTP client implementing IHttpClient
- `ConfigProvider` — JSON config access implementing IConfigProvider
- `ColorFormatter` — ANSI colors implementing IColorFormatter
- `TfidfEmbedder` — TF-IDF text embedding
- `InMemoryVectorStore` — in-memory vector store
- `SummaryService` — conversation summarization
- `BackgroundProcessManager` — background process lifecycle
- `FileWatcherService` — file change watching
- `MemoryService` — persistent memory
- `ContextManager` — context window management
- `ModelLoader` — GGUF model loading
- `TerminalAdapter` — terminal I/O

### 5. Config Layer (Config/)
- `EAgentConfig` — strongly-typed appsettings.json wrapper
- `ConfigLoader` — loads config from JSON (replaces static EAgentConfig.Load)
- `ContextParams` — inference parameters
- `Config/Models/` — 14 config model classes (one per file)

### 6. Memory Layer (Memory/)
- `VectorMemoryStore` — vector storage with cosine similarity
- `EMemoryManager` — persistent memory manager

### 7. Analysis Layer (Analysis/)
- `EContextAnalyzer` — project file analysis and relationship mapping

### 8. Session Layer (Session/)
- `SessionManager` — multi-session lifecycle management
- `AgentSession` — single session state and engine binding
- `SessionDiscovery` — discovers sessions on disk
- `ConsoleUiRenderer` — console UI rendering

### 9. UI Layer (UI/)
- `EGuiBase` — abstract UI base
- `EGuiConsole` — ANSI terminal with scroll regions
- `EColor` — color formatting (instance class implementing IColorFormatter)

## Test Structure

```
Tests/
├── Services/          — 14 unit test files
├── Tools/             — 13 unit test files
├── Engine/            — 12 unit test files
├── Memory/            — 2 unit test files
├── Analysis/          — 1 unit test file
├── Session/           — 2 unit test files
├── Config/            — 2 unit test files
├── Integration/       — 9 integration test files
│   ├── OrchestratorIntegrationTests.cs
│   ├── ToolPipelineIntegrationTests.cs
│   ├── SubAgentIntegrationTests.cs
│   ├── ParallelToolExecutorIntegrationTests.cs
│   ├── SessionManagementIntegrationTests.cs
│   ├── MemoryIntegrationTests.cs
│   ├── ConfigIntegrationTests.cs
│   ├── ContextWindowIntegrationTests.cs
│   └── TranscriptIntegrationTests.cs
└── ECAssistant.Tests.csproj
```

## Remaining Statics (Policy-Compliant)

All remaining statics are pure/stateless with `// Stateless utility` comments:
- `EGuiBase.Truncate()` — pure string utility (14 call sites)
- `EToolResult.Success/Failure` — pure factory on immutable class
- `TranscriptMessage` factories — pure factory on data class
- `ToolPolicy` record factories — pure
- `SecondaryModelLoader.Load()` — pure factory
- `ParallelToolExecutor.CombineResults/FormatConsoleSummary` — pure utilities
- `EGuiConsole` P/Invoke extern methods — language requirement
- `EcaTests` — test fixture (allowed)

**Status:** v11.1 — OOP refactoring complete, 912 tests passing.
**Updated:** 2026-08-15