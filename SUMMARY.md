# ECAssistant — Project Summary (v9.0 — 2026-08-12)

**Summary:** A local, offline AI agent built in C# .NET 8 using LLamaSharp. Embeds a GGUF model into its own runtime. Uses structured XML-style tags for tool calling and multi-step autonomous loops with persistent memory, sliding context windows, real tokenizer-based token counting, tool policy enforcement, multi-session management, background process execution, and structured logging. PowerShell is the primary tool for all file and system operations — no separate file operation classes needed since all LLMs know PowerShell natively. Tools self-register their rules and examples into the system prompt at runtime — SystemPrompt.md is tool-agnostic.

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0-windows`, Nullable enabled)
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk (Qwen3-8B-Q4_K_M.gguf)
- **Runtime:** Self-hosted, offline inference — no external API calls, no prerequisites
- **Model:** Qwen3-8B-Q4_K_M, context: 32768 tokens, temp: 0.3
- **System Prompt:** `SystemPrompt.md` (v3.2 — tool-agnostic, loaded via `File.ReadAllText()` at startup; tools self-register at runtime)
- **Tools:** 2 registered — `EPowerShellAgent` (primary, all file/system ops) + `EFileResearchTool` (project-wide scan)
- **Package deps:** `LLamaSharp`, LLamaSharp.Backend.Vulkan/Cuda12/CPU, Microsoft.Extensions.Logging.Abstractions

## Design Philosophy

**PowerShell as primary tool:** Instead of creating separate C# classes for each file operation (read, write, copy, delete, search), the agent uses `EPowerShellAgent` for everything. Every LLM already knows PowerShell commands (`Get-Content`, `Copy-Item`, `Set-Content`, `Select-String`, etc.), so no extra tool definitions are needed. This keeps the codebase lean and the tool surface small. `EFileResearchTool` is kept for project-wide multi-file scanning that would be inefficient with individual PowerShell commands.

## What's New in v9.0

### Background Process Manager (P1)
- `BackgroundProcessManager` — start, track, and kill long-running processes without blocking
- CLI commands: `bg-run`, `bg-status`, `bg-output`, `bg-kill`, `bg-cleanup`
- Each process gets an ID, timeout (default 300s), and async output capture

### Structured Logging (P2)
- `Logger` — lightweight file + console logger, no external dependencies
- Levels: Debug < Info < Warn < Error (default: Info)
- Logs to `~/ECAssistant/ECAssistant.log` with timestamps
- Wired into engine (startup, context budget), orchestrator (tool execution), PowerShell agent (success/failure)
- CLI commands: `log` (show recent), `log-level` (set level)

### EDecisionLoop v2
- Real user input via EGuiBase (no more hardcoded "B" default)
- LLM-driven analysis — sends task to LLM, relays questions to user, feeds answers back
- Max 5 rounds to prevent infinite loops
- User can type "cancel" to abort

### EContextAnalyzer v2
- Real project analysis: line counts, file types, TODO detection, class/method counts
- Circular dependency detection
- Orphaned file detection (not referenced, not startup)
- Large file warnings (>500 lines)
- Human-readable summary with stats breakdown

### Dead Code Removed
- `EFileOpsTools.cs` deleted — PowerShell handles all file ops (the maintainer's design decision)

## Project Tree
```
ECAssistant/
├── ECAssistant.csproj           ← .NET 8 project (net8.0-windows, Nullable)
├── SystemPrompt.md               ← v3.2: Response format + operating rules (tool-agnostic)
├── appsettings.json              ← Runtime config (model, tools, memory, workspace settings)
├── SUMMARY.md                    ← This file
├── ARCHITECTURE.md               ← Architecture documentation
├── GAP_ANALYSIS.md               ← P0-P3 gap analysis vs OpenClaw
├── GAP_FILTERED.md               ← Filtered gap analysis (local agent scope)
│
├── Program.cs                    ← Entry point: CLI loop, session mgmt, bg exec, logging init
├── Orchestrator.cs                ← v2.3: Block detection + tool policy + approval gates + logging
├── EColor.cs                      ← ANSI-colored console output helpers
│
├── Engine/
│   ├── EAgentEngine.cs           ← Core LLM engine: GGUF model, prompt building, inference
│   ├── ContextWindow.cs          ← Sliding window with real token counting + auto-summarize at 75%
│   ├── ConversationTranscript.cs  ← JSON transcript persistence for session resumption
│   ├── TokenCounter.cs            ← LLamaSharp tokenizer-based counting with char fallback
│   └── EDecisionLoop.cs           ← v2: Interactive decision loop with real user input
│
├── Orchestration/
│   (in Orchestrator.cs)           ← AgentOrchestrator: multi-step loop, tool policy, logging
│
├── Tools/
│   ├── EToolBase.cs               ← Abstract base class for all tools
│   ├── ToolPolicy.cs              ← Permission system: Allowed / ApprovalRequired / Blocked
│   ├── EPowerShell/
│   │   └── EPowerShellAgent.cs    ← PRIMARY TOOL: all file/system ops via PowerShell
│   ├── EResearch/
│   │   └── EFileResearchTool.cs   ← Project-wide file scan + content read
│   └── EExample/
│       └── EFileAnalyzer.cs       ← Example tool (template for new tools)
│
├── Session/
│   └── AgentSession.cs            ← AgentSession + SessionManager (main, isolated, named)
│
├── Services/
│   ├── SummaryService.cs          ← LLM-based context summarization
│   ├── BackgroundProcessManager.cs ← Non-blocking process execution + tracking
│   └── Logger.cs                  ← Structured logging (file + console, no deps)
│
├── Memory/
│   └── EMemoryManager.cs          ← Persistent keyword-based memory across sessions
│
├── Config/
│   ├── EAgentConfig.cs            ← Full config model (nested JSON, matches appsettings.json)
│   └── ContextParams.cs           ← Context parameter helpers
│
├── Analysis/
│   └── EContextAnalyzer.cs        ← v2: Project analysis (lines, TODOs, deps, orphans, cycles)
│
└── UI/
    ├── EGuiBase.cs                ← Abstract UI interface (swap console → GUI → web)
    └── EGuiConsole.cs             ← Console implementation
```

## CLI Commands

| Command | Description |
|---------|-------------|
| `<type request>` | Multi-step agent execution |
| `quit` / `exit` | Exit (saves transcript) |
| `help` | Show help |
| `tools` | List registered tools |
| `clear-history` / `clear-context` | Clear conversation context |
| `save-context` | Save transcript to disk |
| `memory-save` / `memory-query` / `memory-stats` | Memory commands |
| `file-pick` | Open file picker, send content to LLM |
| `sessions` / `session-status` / `session-create` / `session-cleanup` | Session commands |
| `bg-run` / `bg-status` / `bg-output` / `bg-kill` / `bg-cleanup` | Background exec |
| `log` / `log-level` | Logging commands |
| `analyze-project` | Run context analyzer on project |

## Components

### EPowerShellAgent (Primary Tool)
- Executes ANY PowerShell command via temp `.ps1` script file
- Sets `WorkingDirectory` so relative paths work correctly
- Escapes `<` and `>` in output to prevent XML tag confusion
- Handles all file operations: read, write, copy, move, delete, search, compile
- Logs success/failure to structured logger

### BackgroundProcessManager
- Start long-running processes without blocking the main loop
- Track process status (Running, Completed, Failed, TimedOut)
- Get output (stdout + stderr) from finished/running processes
- Kill processes by ID
- Auto-timeout (default 300s)
- Cleanup finished processes from tracking

### Logger
- File-based logging to `~/ECAssistant/ECAssistant.log`
- 4 levels: Debug, Info, Warn, Error
- Console output for Warn/Error only (avoid spam)
- Thread-safe (locked writes)
- No external dependencies

### Tool Policy
- 3 permission levels: `Allowed`, `ApprovalRequired`, `Blocked`
- Checked before every tool execution in orchestrator

### Session Management
- `AgentSession`: owns engine, orchestrator, policy, state
- `SessionManager`: creates/tracks main, isolated, named sessions

### Context Management
- `ContextWindow`: sliding window with real LLamaSharp token counting
- Auto-summarize at 75% budget threshold
- `SummaryService` wired to engine's own LLM for real summarization

### EContextAnalyzer v2
- Scans project files (excludes bin/obj/.git/node_modules)
- Counts lines, classes, methods, TODOs per file
- Detects circular dependencies
- Finds orphaned files (unreferenced, non-startup)
- Warns on large files (>500 lines) and high TODO counts (>3)
- Produces human-readable summary with file type breakdown

**Status:** v9.0 — PowerShell as primary tool. Background exec active. Structured logging active. DecisionLoop v2 with real user input. ContextAnalyzer v2 with real analysis.

## 🔧 Git Workflow

**Repo:** `https://github.com/LLamaDudeX/ECAssistant.git` (branch: `main`)

**Rule:** Always commit and push all changes to GitHub after completing work on this project. This ensures the code is testable on Windows.

```bash
cd <project-root>
git add -A
git commit -m "<descriptive message>"
git push
```