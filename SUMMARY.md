# ECAssistant — Project Summary (v8.1 — 2026-08-12)

**Summary:** A local, offline AI agent built in C# .NET 8 using LLamaSharp. Embeds a GGUF model into its own runtime. Uses structured XML-style tags for tool calling and multi-step autonomous loops with persistent memory, sliding context windows, real tokenizer-based token counting, tool policy enforcement, and multi-session management. PowerShell is the primary tool for all file and system operations — no separate file operation classes needed since all LLMs know PowerShell natively.

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0-windows`, Nullable enabled)
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk (Qwen3-8B-Q4_K_M.gguf)
- **Runtime:** Self-hosted, offline inference — no external API calls, no prerequisites
- **Model:** Qwen3-8B-Q4_K_M, context: 32768 tokens, temp: 0.3
- **System Prompt:** `SystemPrompt.md` (v3.1 — loaded via `File.ReadAllText()` at startup)
- **Tools:** 2 registered — `EPowerShellAgent` (primary, all file/system ops) + `EFileResearchTool` (project-wide scan)
- **Package deps:** `LLamaSharp`, LLamaSharp.Backend.Vulkan/Cuda12/CPU/Console, Microsoft.Extensions.Logging.Abstractions

## Design Philosophy

**PowerShell as primary tool:** Instead of creating separate C# classes for each file operation (read, write, copy, delete, search), the agent uses `EPowerShellAgent` for everything. Every LLM already knows PowerShell commands (`Get-Content`, `Copy-Item`, `Set-Content`, `Select-String`, etc.), so no extra tool definitions are needed. This keeps the codebase lean and the tool surface small. `EFileResearchTool` is kept for project-wide multi-file scanning that would be inefficient with individual PowerShell commands.

## Key Fixes (v8.1)
- **EPowerShellAgent rewritten:** Uses temp `.ps1` script file instead of `cmd.exe` quoting (fixes quote-breaking), sets `WorkingDirectory` on process (fixes relative path issues), escapes `<>` in output (fixes XML tag confusion in LLM history)
- **SystemPrompt v3.1:** Clear PowerShell examples for all file operations, no duplicate tool definitions
- **Removed duplicate tool injection:** `BuildSystemToolsPrompt()` no longer appends tool blocks — tools are documented in SystemPrompt.md only
- **Dead code removed:** `EEngineSystemPrompt.cs` and `SystemPrompt.json` deleted
- **SummaryService wired:** Context compaction now uses the LLM for real summarization

## Project Tree
```
ECAssistant/
├── ECAssistant.csproj           ← .NET 8 project (net8.0-windows, Nullable)
├── SystemPrompt.md               ← v3.1: Identity + response format + PowerShell tool examples
├── appsettings.json              ← Runtime config (model, tools, memory, workspace settings)
├── SUMMARY.md                    ← This file
├── ARCHITECTURE.md               ← Architecture documentation
├── GAP_ANALYSIS.md               ← P0-P3 gap analysis vs OpenClaw
│
├── Program.cs                    ← Entry point: CLI loop, session management, tool registration
├── Orchestrator.cs                ← v2.3: Block detection + tool policy enforcement + approval gates
├── EColor.cs                      ← ANSI-colored console output helpers
│
├── Engine/
│   ├── EAgentEngine.cs           ← Core LLM engine: GGUF model, prompt building, inference
│   │   WireSummaryService(): wires LLM-based context compaction
│   │   BuildSystemToolsPrompt(): system prompt only (no duplicate tool injection)
│   │   BuildFullPrompt(): system → memory injection → windowed history → stop directive
│   ├── ContextWindow.cs          ← Sliding window with real token counting + auto-summarize at 75%
│   ├── ConversationTranscript.cs  ← JSON transcript persistence for session resumption
│   ├── TokenCounter.cs            ← LLamaSharp tokenizer-based counting with char fallback
│   └── EDecisionLoop.cs           ← Interactive decision loop (WIP — placeholder)
│
├── Orchestration/
│   (in Orchestrator.cs)           ← AgentOrchestrator: multi-step loop, tool policy check, approval gates
│
├── Tools/
│   ├── EToolBase.cs               ← Abstract base class for all tools
│   ├── ToolPolicy.cs              ← Permission system: Allowed / ApprovalRequired / Blocked
│   ├── EPowerShell/
│   │   └── EPowerShellAgent.cs    ← PRIMARY TOOL: all file/system ops via PowerShell
│   │   Uses temp .ps1 script (no quoting issues), sets WorkingDirectory, escapes XML in output
│   ├── EResearch/
│   │   └── EFileResearchTool.cs   ← Project-wide file scan + content read (escapes XML)
│   ├── EFileOps/
│   │   └── EFileOpsTools.cs       ← File op tools (NOT registered — available if needed later)
│   └── EExample/
│       └── EFileAnalyzer.cs       ← Example tool (template for new tools)
│
├── Session/
│   └── AgentSession.cs            ← AgentSession + SessionManager (main, isolated, named)
│
├── Memory/
│   └── EMemoryManager.cs          ← Persistent keyword-based memory across sessions
│
├── Services/
│   └── SummaryService.cs          ← LLM-based context summarization (v2: Func<string, Task<string>>)
│
├── Config/
│   ├── EAgentConfig.cs            ← Full config model (nested JSON, matches appsettings.json)
│   └── ContextParams.cs           ← Context parameter helpers
│
├── Analysis/
│   └── EContextAnalyzer.cs        ← Cross-file context analyzer (WIP — basic using parsing)
│
└── UI/
    ├── EGuiBase.cs                ← Abstract UI interface (swap console → GUI → web)
    └── EGuiConsole.cs             ← Console implementation
```

## Components

### EPowerShellAgent (Primary Tool)
- Executes ANY PowerShell command via temp `.ps1` script file
- Sets `WorkingDirectory` so relative paths work correctly
- Escapes `<` and `>` in output to prevent XML tag confusion in LLM conversation history
- Handles all file operations: read, write, copy, move, delete, search, compile

### Tool Policy
- 3 permission levels: `Allowed`, `ApprovalRequired`, `Blocked`
- `EPowerShellAgent`: Allowed (primary tool, needs command access)
- `EFileResearchTool`: Allowed (read-only)
- Policy checked before every tool execution in orchestrator
- Console approval prompt for `ApprovalRequired` tools

### Session Management
- `AgentSession`: owns engine, orchestrator, policy, state
- `SessionManager`: creates/tracks main, isolated, named sessions
- CLI commands: `sessions`, `session-status`, `session-create`, `session-cleanup`

### Context Management
- `ContextWindow`: sliding window with real LLamaSharp token counting
- Auto-summarize at 75% budget threshold (drops oldest 40%, LLM summarizes)
- `SummaryService` wired to engine's own LLM for real summarization
- `ConversationTranscript`: JSON persistence for session resumption

### Memory
- `EMemoryManager`: keyword-based persistent memory across sessions
- Injected into every prompt via `GetMemoryInjection()`
- Categories: bugs, solutions, general, user
- CLI commands: `memory-save`, `memory-query`, `memory-stats`

**Status:** v8.1 — builds successfully (0 errors). PowerShell as primary tool. Tool policy active. Session management active. Context compaction wired.