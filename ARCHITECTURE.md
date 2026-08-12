# ECAssistant Architecture (v9.0 — 2026-08-12)

**Summary:** A local, offline AI agent in C# .NET 8 using LLamaSharp. Runs GGUF models locally. Uses XML-style response tags (`<toolcall>`, `<output>`, `<thinking>`) for reliable tool parsing. PowerShell is the primary and only tool needed for all file/system operations. Multi-step autonomous loops with persistent memory, sliding context windows, real tokenizer-based token counting, tool policy enforcement, multi-session management, background process execution, and structured logging. Tools self-register their rules and examples into the system prompt at runtime.

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0-windows`, Nullable enabled)
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk
- **Runtime:** Self-hosted, offline — no external API calls, no prerequisites
- **Tools:** 2 registered — `EPowerShellAgent` (all file/system ops) + `EFileResearchTool` (project scan)
- **Config:** `SystemPrompt.md` (v3.2 — tool-agnostic), `appsettings.json` (runtime settings)

## Architecture Overview

```
Program.cs (entry point, CLI loop, session mgmt, bg exec, logging init)
    │
    ├── Logger.Initialize() — structured logging to file + console
    │
    ├── SessionManager (creates/manages sessions)
    │       └── AgentSession (main, isolated, named)
    │               │
    │               ├── AgentOrchestrator.ExecuteMultiStep(goal)
    │               │       │
    │               │       ├── ToolPolicy.Check() — permission/approval gate
    │               │       │
    │               │       ├── EAgentEngine.GenerateAsync(prompt)
    │               │       │       │
    │               │       │       ├── BuildFullPrompt(): system prompt → memory → windowed history
    │               │       │       │       (tools self-register via ToSystemPromptBlock())
    │               │       │       │
    │               │       │       └── _executor.InferAsync() — LLamaSharp InteractiveExecutor
    │               │       │               │
    │               │       │               └── ExtractCleanResponse() — strips noise
    │               │       │
    │               │       ├── TrimToFirstClosingTag() — cuts drift
    │               │       ├── ParseLLMDecision() — detects <toolcall>, <output>, or error
    │               │       └── ExecuteTool() — via tool policy → execute → log result
    │               │
    │               ├── ContextWindow — sliding window with real token counting
    │               │       └── SummaryService (LLM-based compaction at 75% budget)
    │               │
    │               └── EMemoryManager — persistent keyword memory, injected every turn
    │
    ├── BackgroundProcessManager — non-blocking process execution
    │       └── Start, track, kill, get output for long-running commands
    │
    ├── CLI Commands: quit/exit, help, tools, sessions, session-*,
    │                  bg-run/status/output/kill/cleanup, log, log-level,
    │                  memory-save/query/stats, file-pick, analyze-project
    │
    └── Tool System
            ├── EPowerShellAgent (PRIMARY — all file/system operations)
            │       ├── Temp .ps1 script (no quoting issues)
            │       ├── WorkingDirectory set (relative paths work)
            │       ├── Escapes <> in output (prevents XML confusion)
            │       └── Logs success/failure to Logger
            │
            ├── EFileResearchTool (project-wide file scan)
            │
            └── EToolBase (abstract base: Name, Description, ExecuteAsync, ToSystemPromptBlock)
```

## Design Decisions

### 1. PowerShell as Primary Tool (v8.1)
**Decision:** Use `EPowerShellAgent` for all file operations instead of separate C# tool classes.

**Rationale:**
- Every LLM already knows PowerShell commands (`Get-Content`, `Copy-Item`, etc.)
- No need to define and maintain separate classes for read/write/copy/delete/search
- PowerShell is far more powerful — piping, variables, loops, conditionals
- Smaller tool surface = less confusion for the LLM
- Adding new file operations = zero code changes (just use a different PowerShell command)

### 2. Tool Self-Registration in System Prompt (v8.2)
**Decision:** SystemPrompt.md is tool-agnostic. Tools self-register at runtime.

**Rationale:** Adding/removing tools requires no SystemPrompt.md edits. Tool definitions are co-located with implementation.

### 3. Background Process Manager (v9.0)
**Decision:** Add non-blocking process execution for long-running commands.

**Rationale:** PowerShell tool blocks the main loop. Background exec enables:
- Starting `dotnet build` without freezing the agent
- Checking status later via `bg-status`
- Getting output via `bg-output`
- Killing via `bg-kill`

### 4. Structured Logging (v9.0)
**Decision:** Lightweight built-in logger instead of Serilog.

**Rationale:** No external dependency, matches the project's self-contained philosophy. File + console output, 4 levels, thread-safe. Sufficient for debugging without adding NuGet packages.

### 5. DecisionLoop v2 (v9.0)
**Decision:** Replace placeholder with real interactive loop.

**Rationale:** v1 always picked "B" with no real user input. v2 sends task to LLM, relays questions to user, feeds answers back. Max 5 rounds, user can cancel.

### 6. EContextAnalyzer v2 (v9.0)
**Decision:** Real project analysis instead of placeholder.

**Rationale:** v1 only parsed `using` statements. v2 counts lines, classes, methods, TODOs, detects circular deps, finds orphaned files, warns on large files.

## Feature Status

| Feature | Status | Notes |
|---------|--------|-------|
| Local GGUF inference | ✅ Live | LLamaSharp 0.27.0 |
| PowerShell tool (primary) | ✅ Live | All file/system ops |
| EFileResearchTool | ✅ Live | Project-wide scan |
| XML-style response parsing | ✅ Live | `<thinking>`, `<toolcall>`, `<output>` |
| Tool self-registration | ✅ Live | Runtime via ToSystemPromptBlock() |
| Multi-step orchestration | ✅ Live | Fail-fast on 3 failures |
| Tool policy + approval gates | ✅ Live | 3 levels |
| Session management | ✅ Live | Main, isolated, named |
| Real tokenizer counting | ✅ Live | LLamaSharp .Tokenize() |
| Memory injection into prompts | ✅ Live | Keyword query every turn |
| Sliding context window | ✅ Live | Auto-summarize at 75% |
| LLM-based summarization | ✅ Live | SummaryService wired to engine |
| Transcript persistence | ✅ Live | JSON disk save/load |
| Abstract UI layer | ✅ Live | EGuiBase interface |
| Background process manager | ✅ Live | Start/track/kill, CLI commands |
| Structured logging | ✅ Live | File+console, 4 levels |
| DecisionLoop v2 | ✅ Live | Real user input, LLM-driven |
| EContextAnalyzer v2 | ✅ Live | Lines, TODOs, deps, orphans, cycles |

**Status:** v9.0 — all gap analysis items addressed. Ready for Windows testing.