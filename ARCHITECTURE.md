# ECAssistant Architecture (v10.4.2 — 2026-08-12)

**Summary:** A local, offline AI agent in C# .NET 8 using LLamaSharp. Runs GGUF models locally with no external API calls. Uses XML-style response tags (`<thinking>`, `<toolcall>`, `<output>`) for reliable tool parsing. 7 registered tools self-register their rules at runtime. Multi-step autonomous loops with dual memory (keyword + TF-IDF vector), sliding context windows with LLM summarization, self-correction with failure loop detection and file rollback, project context awareness with dependency graph, task decomposition, surgical code editing, background process management, file watching, and structured logging. Token-optimized for 8B models (~2825 tokens for system prompt + tools).

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0-windows`, Nullable enabled)
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk
- **Executor:** StatelessExecutor (NOT InteractiveExecutor — see Critical Lesson below)
- **Tools:** 7 registered — EPowerShellAgent, EFileResearchTool, EBackgroundExec, EWebSearch, EDotnetBuild, EGitTool, ECodeEditor
- **Config:** `~/ECAssistant/appsettings.json` (user-editable, bundled as fallback)
- **System Prompt:** SystemPrompt.md v5.1 (~1300 tokens, tool-agnostic)
- **Working Directory:** `~/ECAssistant/` (all disk writes, build dir read-only)
- **Context:** 16384 tokens, max_tokens 2048, auto-summarize at 50%

## 🚨 CRITICAL LESSON — StatelessExecutor vs InteractiveExecutor (2026-08-12)

**This was a major bug that caused zero token output on turn 2+ of every multi-step workflow.**

### The Problem
`InteractiveExecutor` is **stateful** — it maintains KV cache state between `InferAsync()` calls. It's designed for incremental chat: feed the system prompt once, then only feed new user messages as conversation continues.

But ECAssistant's `BuildFullPrompt()` **rebuilds the entire conversation every turn** (system prompt + tools + memory + history + directives). When `InferAsync(fullPrompt)` is called on turn 2, the executor adds the ENTIRE rebuilt prompt on top of turn 1's cached KV state via `_embed_inps.AddRange()`. This double-feeds the context and the model produces **zero tokens**.

### The Fix
Switched to `StatelessExecutor` which creates a **fresh context per `InferAsync` call**. It reprocesses the full prompt from scratch each time — slightly slower but correct for the full-prompt-rebuild architecture.

### Rules to Remember
1. **InteractiveExecutor** = incremental chat (feed once, then new tokens only). KV cache persists between calls.
2. **StatelessExecutor** = full prompt each call. Fresh context per call. No state between calls.
3. If your architecture rebuilds the full prompt every turn → use **StatelessExecutor**.
4. If your architecture feeds incrementally (only new user messages) → use **InteractiveExecutor**.
5. **Never mix** full-prompt-rebuild with a stateful executor — it will silently produce empty output on turn 2+.

### Anti-Prompts Lesson
Also removed `</toolcall>`, `</output>`, and `---` from `InferenceParams.AntiPrompts`. These strings appear in conversation history (which is part of the rebuilt prompt). LLamaSharp's built-in anti-prompt system can match them in the prompt context and stop generation before it starts. Our manual anti-prompt check in the streaming loop already handles `</toolcall>` and `</output>` — keeping them in `InferenceParams.AntiPrompts` was redundant and harmful.

**Safe anti-prompts:** `User:`, `\n```\n`, `Question:` — these don't appear in our prompt format.
**Never use as anti-prompts:** `---`, `</toolcall>`, `</output>` — these appear in SystemPrompt.md and conversation history.

## 🚨 CRITICAL LESSON — Turn Counter Reset Between Requests (2026-08-12)

**This bug caused the model to repeat the old question when a new one was asked in the same session.**

### The Problem
There are two separate `_turnCount` fields:
1. **`AgentOrchestrator._turnCount`** — tracks turns within one `ExecuteMultiStep` call
2. **`EAgentEngine._turnCount`** — tracks which `GenerateAsync` call we're on (used by the `if (_turnCount == 1)` guard that adds the user message to context)

After question 1 took 2 turns, the engine's `_turnCount` was at 2. When the user asked a **new** question, `GenerateAsync` incremented it to 3 — so the `if (_turnCount == 1)` guard **never fired**. The new user message was **never added to the context window**. The model only saw the old conversation and kept repeating it.

### The Fix
`ExecuteMultiStep` now calls `Reset()` + `_engine.ResetTurnCount()` at the start of every new user request. This ensures the first `GenerateAsync` of each new question runs the turn-1 logic (add user message to context).

### Rules to Remember
1. **Any state that affects per-request logic must be reset at the start of each new request.**
2. When you have a turn-1 guard (e.g., `if (_turnCount == 1)`), ensure the counter resets between requests.
3. Don't assume instance fields reset themselves — orchestrators and engines maintain state across calls.
4. Test with sequential questions, not just single questions — single-question tests miss this bug.

## 🚨 CRITICAL LESSON — Generation Cue for Completion Models (2026-08-12)

**This bug caused the model to echo history instead of generating its own response.**

### The Problem
Completion-style LLMs (like Qwen3-8B) need an explicit cue to know when it's their turn to generate. Without a cue, the model sees history ending with `<user>...</user>` and simply echoes it back: `</user><user>what day is today</user>` — repeating the first user message from history.

### The Fix
Append an open `<assistant>` tag at the end of `BuildFullPrompt()` as a generation cue. This tells the model: "history ends here, now it's your turn to respond as the assistant." Also strip leading `<assistant>` and trailing `</assistant>` from raw model output in case the model echoes the cue tag back.

## Architecture Overview

```
Program.cs (entry point, CLI loop, startup, tool registration)
    │
    ├── Startup Flow:
    │   1. Create ~/ECAssistant/ if missing
    │   2. Copy appsettings.json from build dir if missing (first run only)
    │   3. Load config from ~/ECAssistant/appsettings.json (ALWAYS from working dir)
    │   4. Resolve model path: ~/ECAssistant/ first, build dir as fallback
    │   5. Create EAgentEngine (model, 16384 context, 15 GPU layers, working dir)
    │   6. WireSummaryService (LLM-based context compaction)
    │   7. InitializeVectorMemory (TF-IDF vector store in ~/ECAssistant/vecmem/)
    │   8. InitializeSelfCorrection (failure loop detection, snapshots)
    │   9. InitializeProjectContext (auto-scan project, dependency graph)
    │   10. InitializeTaskPlanner (task decomposition)
    │   11. Register 7 tools (self-inject rules into system prompt)
    │   12. Create SessionManager, BackgroundProcessManager, FileWatcherService
    │   13. Start RunAgentLoop
    │
    ├── CLI Loop (RunAgentLoop):
    │   ├── Read user input (> prompt)
    │   ├── Parse command (help, tools, memory, sessions, bg, watch, etc.)
    │   └── If not a command → AgentOrchestrator.ExecuteMultiStep(input)
    │
    └── AgentOrchestrator.ExecuteMultiStep(goal)
        │
        ├── Loop (max 5 turns):
        │   │
        │   ├── 1. EAgentEngine.GenerateAsync(goal)
        │   │   ├── BuildFullPrompt:
        │   │   │   ├── SystemPrompt.md v5.1 (~1300 tokens, tool-agnostic)
        │   │   │   ├── 7 tool self-registrations (~1525 tokens)
        │   │   │   ├── Memory injection (vector top-3 + keyword top-5, code tasks only)
        │   │   │   ├── Project context (file list + deps, code tasks only)
        │   │   │   ├── Task progress (sub-task checklist)
        │   │   │   ├── Failure history (recent failures for context)
        │   │   │   ├── History budget (reserve system + memory + max_tokens + buffer)
        │   │   │   └── Context-aware directive (multi-step vs push-for-output)
        │   │   │
        │   │   ├── Inference: LLamaSharp InteractiveExecutor
        │   │   │   ├── Token streaming to console (dim color, real-time)
        │   │   │   ├── Manual anti-prompt check (break on </toolcall> or </output>)
        │   │   │   └── 90s timeout, 2048 max tokens
        │   │   │
        │   │   └── ExtractCleanResponse: first <thinking> + first <toolcall>/<output> only
        │   │
        │   ├── 2. TrimToFirstClosingTag
        │   ├── 3. ParseLLMDecision: toolcall | output | invalid
        │   │
        │   ├── 4a. Tool call:
        │   │   ├── ToolPolicy.Check (Allowed / ApprovalRequired / Blocked)
        │   │   ├── SelfCorrection.SnapshotFile (before modification)
        │   │   ├── ExecuteTool → run → get result
        │   │   ├── AddToolResult (auto-save transcript)
        │   │   ├── InjectFormatRetry as USER message (not tool output):
        │   │   │   "You MUST respond with <thinking>...</thinking><output>...</output>"
        │   │   ├── SelfCorrection.RecordFailure (on failure, check for loops)
        │   │   │   → 3x same error → escalate to user
        │   │   │   → Alternating pattern → escalate
        │   │   └── Continue loop
        │   │
        │   ├── 4b. Direct answer (<output>):
        │   │   └── Return OrchestratorResult (goal achieved)
        │   │
        │   └── 4c. Invalid (no tags):
        │       ├── RemoveLastAssistantResponse (from context window + transcript)
        │       ├── InjectFormatRetry as user message (strong format reminder)
        │       ├── Retry up to 2 times
        │       └── After 2 failures → stop with error
```

## Tool System (7 Tools)

```
EToolBase (abstract) — Name, Description, UsageExample, GetToolRules(), GetToolExample()
├── ToSystemPromptBlock() — assembles all into system prompt at runtime

1. EPowerShellAgent     — file ops, shell commands, 60s timeout
2. EFileResearchTool     — project-wide file scan
3. EBackgroundExec      — background process management (LLM-callable)
4. EWebSearch           — DuckDuckGo web search (no auth)
5. EDotnetBuild         — build/test/test-filter/format with structured error parsing
6. EGitTool             — git operations with structured output
7. ECodeEditor          — surgical code editing: patch/diff/search/replace-all/insert/delete-lines

ToolPolicy: 3 levels (Allowed / ApprovalRequired / Blocked) checked before every execution
```

## Memory System (Dual)

### Keyword Memory (EMemoryManager)
- JSON files on disk (`~/ECAssistant/Memory/`)
- Relevance scoring: key match (3pts), word match (2pts), content match (1pt) + confidence weighting
- Injected into every prompt (top 5)

### Vector Memory (VectorMemoryStore)
- TF-IDF embeddings (256-dim hash-based, L2 normalized)
- Cosine similarity for search ranking
- JSON storage (`~/ECAssistant/vecmem/vectors.json`)
- No external dependencies
- Injected into every prompt (top 3)

## Agentic Capabilities (v10)

### Self-Correction (SelfCorrectionManager)
- **Failure loop detection:** same error 3x → escalate, same tool failing 3x → escalate
- **Alternating pattern detection:** fix A breaks B pattern → escalate
- **File snapshots:** before modification, rollback on failure
- **Failure history:** injected into prompts for context

### Project Context (ProjectContextManager)
- **Auto-scan:** on startup, scans all .cs/.md/.json/.ps1 files (excludes bin/obj/.git)
- **Dependency graph:** built from `using` statements
- **Impact analysis:** "changing file X may affect files Y, Z"
- **Prompt injection:** file list + dependencies (code tasks only — keyword gated)
- **Persistence:** saves to `.project_context.json`

### Task Decomposition (TaskPlanner)
- **Split on indicators:** "then", "and then", "after that", "also", "finally", "next"
- **Action counting:** detects 2+ action verbs (build, create, fix, etc.)
- **Progress tracking:** checklist with ✅❌🔄⬜ status, injected into prompt
- **Adapt:** marks failed sub-tasks, continues with remaining

### Surgical Code Editing (ECodeEditor)
- **patch:** replace old_text → new_text (multi-line, uniqueness check, diff preview)
- **search:** find pattern across all files with file filter
- **replace-all:** replace pattern across all files
- **insert:** insert text at specific line number
- **delete-lines:** remove range of lines
- **diff:** show diff between current file and new content

## Context Management

- **ContextWindow:** sliding window, real LLamaSharp token counting, auto-summarize at 50%
- **OverflowStrategy:** TruncateAndReprefill (not ThrowException)
- **Hard history budget:** reserves system + memory + max_tokens + buffer, trims oldest
- **RemoveLastAssistantMessage:** removes bad tagless response from history for format retries
- **SummaryService:** LLM-based compaction (uses engine's own model)
- **Transcript:** auto-saved on every tool call (crash recovery), JSON format

## Design Decisions

1. **PowerShell as primary tool** — no separate file op classes
2. **Tool self-registration** — SystemPrompt.md is tool-agnostic
3. **Dual memory** — keyword + TF-IDF vector, no external deps
4. **Manual anti-prompt enforcement** — check after each token for closing tags
5. **ExtractCleanResponse** — first block only, prevents repetition
6. **Format retry with history cleanup** — remove bad response, inject as user msg, retry 2x
7. **Post-tool directive as user message** — not inside tooloutput tags
8. **Working directory isolation** — all writes to ~/ECAssistant/, build dir read-only
9. **Safe LLM defaults** — 16K context, 15 GPU layers, 2048 max_tokens, 50% summarize
10. **Token optimization** — system prompt ~1300 tokens, tools ~1525, total ~2825
11. **Project context gated** — only injected for code-related queries (keyword detection)
12. **Self-correction escalation** — 3 repeated failures → stop and ask user
13. **StatelessExecutor (v10.4.2)** — full-prompt-rebuild architecture requires stateless executor. InteractiveExecutor's KV cache persistence breaks multi-turn workflows.
14. **Turn-1-only user message injection** — GenerateAsync only adds the user prompt to context on turn 1. On turns 2+, context is populated by AddToolResult + InjectFormatRetry.
15. **Safe anti-prompts only** — never put strings that appear in SystemPrompt.md or conversation history (like `---`, `</toolcall>`, `</output>`) in InferenceParams.AntiPrompts.
16. **Reset turn counters per request (v10.4.4)** — ExecuteMultiStep calls Reset() + _engine.ResetTurnCount() at start. Two separate _turnCount fields (orchestrator + engine) must both reset between user requests.
17. **Generation cue tag (v10.4.3)** — Append open `<assistant>` at end of BuildFullPrompt() to tell completion models it's their turn. Without it, the model echoes history instead of generating.

## Feature Status (35 features)

| Feature | Status |
|---------|--------|
| Local GGUF inference | ✅ |
| PowerShell tool (primary, 60s timeout) | ✅ |
| EFileResearchTool | ✅ |
| EBackgroundExec | ✅ |
| EWebSearch | ✅ |
| EDotnetBuild (build/test/format/test-filter) | ✅ |
| EGitTool | ✅ |
| ECodeEditor (patch/diff/search/replace/insert/delete) | ✅ |
| XML response parsing (strict) | ✅ |
| Tool self-registration | ✅ |
| Multi-step orchestration (5 turns, 3 before push) | ✅ |
| Tool policy + approval gates | ✅ |
| Format retry (2x, history cleanup, user msg) | ✅ |
| Post-tool output directive (as user msg) | ✅ |
| Session management | ✅ |
| Real tokenizer counting | ✅ |
| Keyword memory (relevance scored) | ✅ |
| Vector memory (TF-IDF, no deps) | ✅ |
| Sliding context window (50% summarize) | ✅ |
| LLM-based summarization | ✅ |
| Transcript auto-save (per tool call) | ✅ |
| Background process manager | ✅ |
| File watcher | ✅ |
| Structured logging | ✅ |
| DecisionLoop v2 | ✅ |
| EContextAnalyzer v2 | ✅ |
| Secondary model support | ✅ |
| Config hot-reload | ✅ |
| Model hot-swap | ✅ |
| Clipboard support | ✅ |
| Working directory isolation | ✅ |
| Auto-create working dir + config | ✅ |
| Token streaming | ✅ |
| Self-correction (failure loops, rollback) | ✅ |
| Project context (scan, deps, impact) | ✅ |
| Task decomposition (sub-tasks) | ✅ |

**Status:** v10.4.4 — All Tier 1-3 agentic capabilities implemented. 7 tools. Multi-turn workflow fixed (StatelessExecutor + turn counter reset + generation cue). Ready for Windows testing.