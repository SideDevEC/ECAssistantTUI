# ECAssistant Architecture (v10.12.20 — 2026-08-12)

**Summary:** A local, offline AI agent in C# .NET 8 using LLamaSharp. Runs GGUF models locally with no external API calls. Uses `<llm>` container tag for noise-proof response parsing with XML-style inner tags (`<thinking>`, `<toolcall>`, `<output>`). 7 registered tools self-register their rules at runtime. Multi-step autonomous loops with dual memory (keyword + TF-IDF vector), sliding context windows with LLM summarization, self-correction with failure loop detection and file rollback, project context awareness with dependency graph, task decomposition, surgical code editing, background process management, file watching, and structured logging. Token-optimized for 8B models. Secondary model (Phi-4-mini) with fully configurable sampling params and anti-prompts.

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0-windows`, Nullable enabled)
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk
- **Executor:** InteractiveExecutor with KV cache (static prefix prefilled once, incremental feed per turn)
- **Secondary Executor:** StatelessExecutor (for summaries/decomposition, no cache)
- **Secondary Model:** Phi-4-mini-instruct-Q4_K_M (decomposition + summarization)
- **Tools:** 7 registered — EPowerShellAgent, EFileResearchTool, EBackgroundExec, EWebSearch, EDotnetBuild, EGitTool, ECodeEditor
- **Config:** `~/ECAssistant/appsettings.json` (user-editable, bundled as fallback)
- **System Prompt:** SystemPrompt.md v6 (~1300 tokens, tool-agnostic)
- **Working Directory:** `~/ECAssistant/` (all disk writes, build dir read-only)
- **Context:** 16384 tokens, max_tokens 2048, auto-summarize at 50%
- **Repo:** `github.com/LLamaDudeX/ECAssistant.git` (branch: `main`)

## 📦 `<llm>` Container Tag System (v10.12)

**The core parsing boundary.** All model responses are wrapped in `<llm>...</llm>`. Everything inside is parsed, everything outside is ignored as noise.

**Response format:**
```
<llm><thinking>Brief reasoning</thinking><toolcall>ToolName<arg>value</arg></toolcall></llm>
<llm><thinking>Brief reasoning</thinking><output>Answer to user</output></llm>
```

**Architecture flow:**
```
Model output → ExtractCleanResponse (<llm> extraction + inner tag parsing) → ParseLLMDecision
```

- **Stop tag:** Only `</llm>` — no fallbacks. Clean and predictable.
- **ExtractCleanResponse:** Extracts content between `<llm>` and `</llm>`, then parses inner tags (`<thinking>`, `<toolcall>`, `<output>`). Falls back to raw if no `<llm>` found (format retries).
- **Generation cue:** `<assistant><llm>` — tells model to open `<llm>` as first token.
- **History rendering:** Past assistant responses wrapped as `<assistant><llm>...</llm></assistant>`.
- **Directives:** All 7 turn directives show full structural example: `Open <llm><thinking>...</thinking><toolcall>...</toolcall></llm>`.
- **No TrimToFirstClosingTag:** Removed. ExtractCleanResponse handles everything. No double-trimming.
- **Future parallelism ready:** `</toolcall>` is NOT a stop tag. Multiple toolcalls can exist inside one `<llm>` container without early stops.

## 🚨 CRITICAL LESSONS

### 1. StatelessExecutor vs InteractiveExecutor (2026-08-12)
**InteractiveExecutor** = incremental chat (feed once, then new tokens only). KV cache persists.
**StatelessExecutor** = full prompt each call. Fresh context per call. No state.
ECAssistant uses InteractiveExecutor with KV cache prefill (static prefix cached once, incremental feed per turn).
**Never mix** full-prompt-rebuild with a stateful executor — silently produces empty output on turn 2+.

### 2. Turn Counter Reset Between Requests (2026-08-12)
Two separate `_turnCount` fields (orchestrator + engine) must both reset between user requests. `ExecuteMultiStep` calls `Reset()` + `_engine.ResetTurnCount()` at start. Without this, the turn-1 guard (`if (_turnCount == 1)`) never fires for new questions.

### 3. Generation Cue for Completion Models (2026-08-12)
Append `<assistant><llm>` as generation cue. Without it, the model echoes history instead of generating. Also strip leading `<assistant>` from raw output if model echoes it.

### 4. Anti-Prompts — Safe vs Dangerous (2026-08-12)
**Safe:** `User:`, `\n```\n`, `Question:`, `### User`, `<user>` — don't appear in prompt format.
**Never use:** `---`, `</toolcall>`, `</output>`, `</llm>` — appear in SystemPrompt.md and history. LLamaSharp's built-in anti-prompt system can match them in prompt context and stop generation before it starts. Our manual stop tag check handles `</llm>` in the streaming loop.

### 5. ESC Stop Must Bail Out Immediately (v10.11.1)
When ESC is pressed during generation, `GenerateAsync` returns `"(Stopped by user)"`. The orchestrator must detect this and return immediately — NOT attempt format retries (which would fail and corrupt state). After ESC: clear context window, rebuild KV cache, reset for next command.

### 6. PowerShell Non-Terminating Errors (v10.11.2)
`New-Item` with a bad path writes to stderr but may exit with code 0. Fix: `$ErrorActionPreference = 'Continue'` (all commands run, errors collected at end) + check stderr + try/catch wrapper. Never use `'Stop'` — it kills the script at first error, remaining `;`-separated commands never execute.

### 7. `<llm>` Container — No Fallback Stop Tags (v10.12.15)
Only `</llm>` is a stop tag. `</output>` as a fallback could cause early stops if the model writes `</output>` inside content (code examples, HTML). If model forgets `</llm>`, generation runs to max_tokens then stops — `ExtractCleanResponse` still parses content inside `<llm>`.

## Architecture Overview

```
Program.cs (entry point, CLI loop, startup, tool registration)
    │
    ├── Startup Flow:
    │   1. Create ~/ECAssistant/ if missing
    │   2. Copy appsettings.json from build dir if missing (first run only)
    │   3. Load config from ~/ECAssistant/appsettings.json
    │   4. Resolve model path: ~/ECAssistant/ first, build dir as fallback
    │   5. Create EAgentEngine (model, 16384 context, 15 GPU layers, working dir)
    │   6. Load secondary model (Phi-4-mini, configurable sampling params + anti-prompts)
    │   7. WireSummaryService (LLM-based context compaction)
    │   8. InitializeVectorMemory (TF-IDF vector store)
    │   9. InitializeSelfCorrection (failure loop detection, snapshots)
    │   10. InitializeProjectContext (auto-scan project, dependency graph)
    │   11. InitializeTaskPlanner (task decomposition)
    │   12. Register 7 tools (self-inject rules into system prompt)
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
        │   │   ├── BuildIncrementalInput (turn 1: user msg + directive + cue)
        │   │   │                       (turn 2+: tool output + directive + cue)
        │   │   ├── Inference: LLamaSharp InteractiveExecutor
        │   │   │   ├── Token streaming to console (dim color, real-time)
        │   │   │   ├── Manual stop tag check: only </llm>
        │   │   │   ├── ESC key detection → _escPressed → bail out
        │   │   │   └── 90s timeout, 2048 max tokens
        │   │   └── ExtractCleanResponse: extract <llm>...</llm> → parse inner tags
        │   │
        │   ├── 2. ESC stop check → if stopped: clear context, rebuild cache, return
        │   ├── 3. ParseLLMDecision: toolcall | output | invalid
        │   │
        │   ├── 4a. Tool call:
        │   │   ├── ToolPolicy.Check (Allowed / ApprovalRequired / Blocked)
        │   │   ├── SelfCorrection.SnapshotFile (before modification)
        │   │   ├── ExecuteTool → run → get result
        │   │   ├── AddToolResult (auto-save transcript, EGuiBase.Truncate for console)
        │   │   ├── BuildStepDirective (with [TASK PROGRESS] check)
        │   │   ├── SelfCorrection.RecordFailure (on failure, check for loops)
        │   │   └── Continue loop
        │   │
        │   ├── 4b. Direct answer (<output>):
        │   │   └── Return OrchestratorResult (goal achieved)
        │   │
        │   └── 4c. Invalid (no tags):
        │       ├── RemoveLastAssistantResponse (from context + transcript)
        │       ├── InjectFormatRetry (with <llm> wrapper example)
        │       ├── Retry up to 2 times
        │       └── After 2 failures → stop with error
```

## Tool System (7 Tools)

```
EToolBase (abstract) — Name, Description, UsageExample, GetToolRules(), GetToolExample()
├── ToSystemPromptBlock() — assembles all into system prompt at runtime

1. EPowerShellAgent     — file ops, shell commands, 60s timeout, Continue+try/catch
2. EFileResearchTool     — project-wide file scan
3. EBackgroundExec      — background process management
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

## Agentic Capabilities

### Self-Correction (SelfCorrectionManager)
- Failure loop detection: same error 3x → escalate, same tool failing 3x → escalate
- Alternating pattern detection: fix A breaks B → escalate
- File snapshots before modification, rollback on failure
- Failure history injected into prompts for context

### Project Context (ProjectContextManager)
- Auto-scan: on startup, scans all .cs/.md/.json/.ps1 files
- Dependency graph from `using` statements
- Impact analysis: "changing file X may affect files Y, Z"
- Prompt injection: file list + dependencies (code tasks only — keyword gated)

### Task Decomposition (TaskPlanner)
- LLM-based decomposition (secondary model) with keyword fallback
- Split on indicators: "then", "and then", "after that", "also", "finally", "next"
- Progress tracking: checklist with ✅❌🔄⬜ status
- Step-aware directives with [TASK PROGRESS] and completion check
- "IMPORTANT: Check [TASK PROGRESS] before deciding. If any step is [ ] or [...], NOT done."

### Surgical Code Editing (ECodeEditor)
- patch: replace old_text → new_text (multi-line, uniqueness check, diff preview)
- search: find pattern across all files with file filter
- replace-all: replace pattern across all files
- insert: insert text at specific line number
- delete-lines: remove range of lines
- diff: show diff between current file and new content

## Context Management

- **ContextWindow:** sliding window, real LLamaSharp token counting, auto-summarize at 50%
- **OverflowStrategy:** TruncateAndReprefill
- **Hard history budget:** reserves system + memory + max_tokens + buffer, trims oldest
- **RemoveLastAssistantMessage:** removes bad response from history for format retries
- **SummaryService:** LLM-based compaction (uses engine's own model)
- **Transcript:** auto-saved on every tool call (crash recovery), JSON format
- **KV cache overflow:** at 80%, summarize with secondary model, rebuild cache, re-inject
- **KV cache rewind:** SaveState before generation, LoadState to rewind on format retry
- **KV cache rebuild after ESC:** RebuildCacheAfterStopAsync re-prefills static prefix fresh

## Secondary Model (v10.12.12+)

Fully configurable via `appsettings.json`:
```json
"secondary_model": {
    "enabled": true,
    "model_path": "Phi-4-mini-instruct-Q4_K_M.gguf",
    "context_size": 4096,
    "gpu_layers": 0,
    "temperature": 0.1,
    "top_p": 0.8,
    "top_k": 40,
    "repeat_penalty": 1.1,
    "max_tokens": 512,
    "anti_prompts": ["User:", "\n```\n", "Question:", "Assistant:", "###", "<user>", "<tooloutput>", "### User"]
}
```

All derived values scale relative to config:
- GenerateAsync default: MaxTokens from config
- SummarizeAsync: 25% of MaxTokens (min 100)
- DecomposeTaskAsync: 50% of MaxTokens (min 128)
- convText cap: 75% of ContextSize in chars
- Summary call: ContextSize/8 (min 100)

## Console Output (v10.12.20)

Centralized truncation in `EGuiBase`:
- `EGuiBase.Truncate(text, maxChars)` — static, returns truncated + `[...]` string
- `WriteLineColored(text, maxChars)` — virtual, truncates then writes
- `WriteLine(text, maxChars)` — virtual, truncates then writes
- All 9 call sites use `EGuiBase.Truncate()` — one place to change behavior

## Design Decisions

1. **PowerShell as primary tool** — no separate file op classes
2. **Tool self-registration** — SystemPrompt.md is tool-agnostic
3. **Dual memory** — keyword + TF-IDF vector, no external deps
4. **`<llm>` container (v10.12)** — noise-proof response parsing, one stop tag, no fallbacks
5. **ExtractCleanResponse** — single extraction point, no double-trimming
6. **Format retry with history cleanup** — remove bad response, inject as user msg, retry 2x
7. **Post-tool directive as user message** — not inside tooloutput tags
8. **Working directory isolation** — all writes to ~/ECAssistant/, build dir read-only
9. **InteractiveExecutor with KV cache (v10.5)** — static prefix prefilled once, incremental feed per turn
10. **Turn-1-only user message injection** — only added to context on turn 1
11. **Safe anti-prompts only** — never strings that appear in SystemPrompt.md or history
12. **ESC stop bails out immediately (v10.11.1)** — no format retries, clear context + rebuild cache
13. **PowerShell Continue mode (v10.12.10)** — all commands run, errors collected at end
14. **Secondary model fully configurable (v10.12.12+)** — sampling params + anti-prompts in appsettings.json
15. **Relative limits (v10.12.13)** — all secondary model limits scale with config values
16. **Centralized truncation (v10.12.20)** — EGuiBase.Truncate, one place, [...] indicator

## Feature Status (40+ features)

| Feature | Status |
|---------|--------|
| Local GGUF inference | ✅ |
| PowerShell tool (Continue+try/catch, 60s timeout) | ✅ |
| EFileResearchTool | ✅ |
| EBackgroundExec | ✅ |
| EWebSearch | ✅ |
| EDotnetBuild | ✅ |
| EGitTool | ✅ |
| ECodeEditor | ✅ |
| `<llm>` container tag system | ✅ |
| Tool self-registration | ✅ |
| Multi-step orchestration (5 turns) | ✅ |
| Tool policy + approval gates | ✅ |
| Format retry (2x, <llm> wrapper example) | ✅ |
| Post-tool directive with [TASK PROGRESS] check | ✅ |
| Session management | ✅ |
| Real tokenizer counting | ✅ |
| Keyword memory | ✅ |
| Vector memory (TF-IDF) | ✅ |
| Sliding context window (50% summarize) | ✅ |
| LLM-based summarization | ✅ |
| Transcript auto-save | ✅ |
| Background process manager | ✅ |
| File watcher | ✅ |
| Structured logging | ✅ |
| Self-correction (failure loops, rollback) | ✅ |
| Project context (scan, deps, impact) | ✅ |
| Task decomposition (LLM + keyword fallback) | ✅ |
| ESC stop with clean state recovery | ✅ |
| Configurable secondary model (sampling + anti-prompts) | ✅ |
| Centralized console truncation | ✅ |
| Config hot-reload | ✅ |
| Model hot-swap | ✅ |
| KV cache prefill + incremental feed | ✅ |
| KV cache overflow handling | ✅ |
| KV cache rewind on format retry | ✅ |
| KV cache rebuild after ESC stop | ✅ |
| Smart tool output truncation | ✅ |
| Tool output escaping | ✅ |
| Clipboard support | ✅ |
| Working directory isolation | ✅ |

**Status:** v10.12.20 — `<llm>` container system, ESC fix, PowerShell error handling, configurable secondary model, centralized truncation. Codebase audited clean (0 bugs, 10 pre-existing warnings). Ready for production testing.

## 🔖 Known-Good Builds (Git Tags)

| Tag | Version | Description |
|-----|---------|-------------|
| `v10.12.20-working` | v10.12.20 | Centralized truncation, audited clean (current) |
| `v10.12.17-working` | v10.12.17 | Restored primary anti-prompts |
| `v10.12.15-working` | v10.12.15 | Only </llm> stop tag, no fallbacks |
| `v10.12.11-working` | v10.12.11 | Console display fix (500→2000) |
| `v10.12.7-working` | v10.12.7 | Removed TrimToFirstClosingTag |
| `v10.12.6-working` | v10.12.6 | 3 audit bugs fixed |
| `v10.12.5-working` | v10.12.5 | Full <llm> structural directives |
| `v10.11.2-working` | v10.11.2 | PowerShell error handling |
| `v10.11.1-working` | v10.11.1 | ESC stop fix |
| `v10.9.4-working` | v10.9.4 | Pre-<llm> baseline |

**If any change breaks multi-turn:**
```bash
git checkout v10.12.20-working  # Current (audited clean)
git checkout v10.11.2-working  # PowerShell error handling
git checkout v10.11.1-working  # ESC fix
git checkout v10.9.4-working   # Pre-<llm> baseline
```