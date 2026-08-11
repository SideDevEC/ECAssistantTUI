# ECAssistant Architecture (v8.1 — 2026-08-12)

**Summary:** A local, offline AI agent in C# .NET 8 using LLamaSharp. Runs GGUF models locally. Uses XML-style response tags (`<toolcall>`, `<output>`, `<thinking>`) for reliable tool parsing. PowerShell is the primary and only tool needed for all file/system operations — no separate file operation classes since all LLMs know PowerShell natively. Multi-step autonomous loops with persistent memory, sliding context windows, real tokenizer-based token counting, tool policy enforcement, and multi-session management.

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0-windows`, Nullable enabled)
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk
- **Runtime:** Self-hosted, offline — no external API calls, no prerequisites
- **Tools:** 2 registered — `EPowerShellAgent` (all file/system ops) + `EFileResearchTool` (project scan)
- **Config:** `SystemPrompt.md` (v3.1), `appsettings.json` (runtime settings)

## Architecture Overview

```
Program.cs (entry point, CLI loop, session management)
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
    │               │       │       ├── BuildFullPrompt(): system prompt → memory → windowed history → stop directive
    │               │       │       │       (no duplicate tool injection — tools documented in SystemPrompt.md only)
    │               │       │       │
    │               │       │       └── _executor.InferAsync(fullPrompt) — LLamaSharp InteractiveExecutor
    │               │       │               │
    │               │       │               └── ExtractCleanResponse() — strips noise, extracts structured blocks
    │               │       │                       │
    │               │       │                       └── Store in transcript + context window
    │               │       │
    │               │       ├── TrimToFirstClosingTag() — cuts drift after first </toolcall> or </output>
    │               │       ├── ParseLLMDecision() — detects <toolcall>, <output>, or error
    │               │       └── ExecuteTool(toolName, args) — via tool policy check → execute
    │               │
    │               ├── ContextWindow — sliding window with real token counting
    │               │       └── SummaryService (LLM-based compaction at 75% budget)
    │               │
    │               └── EMemoryManager — persistent keyword memory, injected every turn
    │
    ├── CLI Commands: quit/exit, help, tools, sessions, session-status,
    │                  clear-history, save-context, memory-save/query/stats,
    │                  file-pick, analyze-project, interactive-decision
    │
    └── Tool System
            ├── EPowerShellAgent (PRIMARY — all file/system operations)
            │       ├── Writes command to temp .ps1 file (avoids quoting issues)
            │       ├── Sets WorkingDirectory on process (relative paths work)
            │       ├── Escapes <> in output (prevents XML tag confusion in LLM history)
            │       └── Handles: Get-Content, Set-Content, Copy-Item, Move-Item,
            │           Remove-Item, Get-ChildItem, Select-String, dotnet build, etc.
            │
            ├── EFileResearchTool (project-wide file scan for analysis)
            │       └── Escapes <> in file content output
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

**Implementation:**
- Command written to temp `.ps1` file → executed with `powershell.exe -File`
- `WorkingDirectory` set on `ProcessStartInfo` so relative paths work
- Output escaped (`<` → `&lt;`, `>` → `&gt;`) to prevent XML tag confusion

### 2. No Duplicate Tool Injection (v8.1)
**Decision:** Tools are documented only in `SystemPrompt.md`, not injected twice by `BuildSystemToolsPrompt()`.

**Rationale:** Duplicating tool definitions in two different formats confused the LLM. SystemPrompt.md v3.1 has clear examples for each tool.

### 3. XML Escaping on Tool Output (v8.1)
**Decision:** All tool output that may contain `<` or `>` characters is escaped before returning to the LLM.

**Rationale:** File content with angle brackets (C# generics, HTML, XML) was being interpreted as XML tags by the LLM's response parser, causing "no valid block detected" errors.

### 4. Tool Policy with Approval Gates (v8)
**Decision:** 3-level permission system (Allowed/ApprovalRequired/Blocked) checked before every tool execution.

**Rationale:** Safety-by-default without blocking useful operations. Currently `EPowerShellAgent` is `Allowed` (agent needs command access to be useful).

### 5. Session Abstraction (v8)
**Decision:** `AgentSession` + `SessionManager` as first-class objects.

**Rationale:** Enables future multi-session support (main, isolated sub-agents, named persistent sessions). Currently all sessions share the same LLamaSharp engine.

## Component Details

### EPowerShellAgent — The Primary Tool
- Writes command to temp `.ps1` script (no `cmd.exe` quoting issues)
- Sets `WorkingDirectory` on process so relative paths work
- Escapes `<>` in output to prevent XML confusion
- Handles ALL file operations: read, write, copy, move, delete, search, compile, run

### ToolPolicy — Permission System
- `Allowed`: tool runs freely
- `ApprovalRequired`: user sees command, must approve with [y/N]
- `Blocked`: tool cannot run
- Checked in orchestrator before every tool call

### SessionManager + AgentSession
- `SessionManager`: creates, tracks, cleans up sessions
- `AgentSession`: owns engine ref, orchestrator, policy, state, metadata
- Types: Main (primary), Isolated (sub-agent), Named (persistent)
- CLI: `sessions`, `session-status`, `session-create`, `session-cleanup`

### ContextWindow + SummaryService
- Real token counting via LLamaSharp tokenizer
- Auto-summarize at 75% budget (drops oldest 40%, LLM summarizes)
- `SummaryService` wired to engine's own LLM via `WireSummaryService()`

### EMemoryManager
- Keyword-based persistent memory (JSON files on disk)
- Injected into every prompt via `GetMemoryInjection()`
- Categories: bugs, solutions, general, user

## Feature Status

| Feature | Status | Notes |
|---------|--------|-------|
| Local GGUF inference | ✅ Live | LLamaSharp 0.27.0, Vulkan/CPU/CUDA backends |
| PowerShell tool (primary) | ✅ Live | Temp .ps1 script, WorkingDirectory, XML escaping |
| EFileResearchTool | ✅ Live | Project-wide scan, XML-escaped output |
| XML-style response parsing | ✅ Live | `<thinking>`, `<toolcall>`, `<output>` |
| Multi-step orchestration | ✅ Live | Fail-fast on 3 consecutive failures |
| Tool policy + approval gates | ✅ Live | 3 levels, checked before every tool call |
| Session management | ✅ Live | Main, isolated, named. CLI commands |
| Real tokenizer counting | ✅ Live | LLamaSharp `.Tokenize().ToList().Count` |
| Memory injection into prompts | ✅ Live | Keyword query, injected every turn |
| Sliding context window | ✅ Live | Auto-summarize at 75%, drops 40% oldest |
| LLM-based summarization | ✅ Live | SummaryService wired to engine |
| Transcript persistence | ✅ Live | JSON disk save/load |
| Abstract UI layer | ✅ Live | EGuiBase interface, EGuiConsole concrete |
| DecisionLoop | ⚠️ WIP | Placeholder — defaults "B", no real user input |
| EContextAnalyzer | ⚠️ WIP | Basic `using` parsing only |

**Status:** v8.1 — builds successfully (0 errors, 10 warnings). PowerShell as primary tool.