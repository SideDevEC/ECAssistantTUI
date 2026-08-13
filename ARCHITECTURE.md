# ECAssistant Architecture (v10.16.0 — 2026-08-13)

**Summary:** A local, offline AI agent in C# .NET 8 using LLamaSharp. Runs GGUF models locally with no external API calls. Uses `<lm>` container tag for noise-proof response parsing with XML-style inner tags (`<thinking>`, `<toolcall>`, `<output>`). 7 registered tools self-register their rules at runtime. Multi-step autonomous loops with dual memory (keyword + TF-IDF vector), sliding context windows with LLM summarization, self-correction with failure loop detection and file rollback, project context awareness with dependency graph, task decomposition, surgical code editing, background process management, file watching, and structured logging. Token-optimized for 8B models. Secondary model (Phi-4-mini) with fully configurable sampling params and anti-prompts. v10.13: Parallel multi-tool execution. v10.16: Cross-platform (Windows + macOS), EShellAgent replaces EPowerShellAgent, dual system prompts, `<lm>` tag renamed from `<llm>` to eliminate double-l hallucination.

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0`, cross-platform, Nullable enabled)
- **Platforms:** Windows (PowerShell) + macOS (zsh) — OS detected at runtime
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk
- **Backends:** Cuda12 (Windows/NVIDIA), Vulkan (all platforms/Metal), Cpu (always)
- **Executor:** InteractiveExecutor with KV cache (static prefix prefilled once, incremental feed per turn)
- **Secondary Executor:** StatelessExecutor (for summaries/decomposition, no cache)
- **Primary Model:** Qwen_Qwen3-8B-Q4_K_M (bartowski) — `/Users/localdev/Agent/models/`
- **Secondary Model:** microsoft_Phi-4-mini-instruct-Q4_K_M (bartowski) — same folder
- **Tools:** 7 registered — EShellAgent, EFileResearchTool, EBackgroundExec, EWebSearch, EDotnetBuild, EGitTool, ECodeEditor
- **Config:** `~/ECAssistant/appsettings.json` (user-editable, bundled as fallback)
- **System Prompts:** SystemPrompt.Windows.md (PowerShell examples) + SystemPrompt.Mac.md (zsh examples) — OS-specific, fallback to SystemPrompt.md
- **Working Directory:** `~/ECAssistant/` (all disk writes, build dir read-only)
- **Context:** 16384 tokens, max_tokens 2048, auto-summarize at 50%
- **Repo:** `github.com/LLamaDudeX/ECAssistant.git` (branch: `main`)

## 📦 `<lm>` Container Tag System (v10.15.2)

**The core parsing boundary.** All model responses are wrapped in `<lm>...</lm>`. Everything inside is parsed, everything outside is ignored as noise.

**Response format:**
```
<lm><thinking>Brief reasoning</thinking><toolcall>ToolName<arg>value</arg></toolcall></lm>
<lm><thinking>Brief reasoning</thinking><output>Answer to user</output></lm>
```

**Architecture flow:**
```
Model output → ExtractCleanResponse (<lm> extraction + inner tag parsing) → ParseLLMDecision
```

- **Stop tag:** Only `</lm>` — no fallbacks. Clean and predictable.
- **Generation cue:** `<assistant>` only (v10.15.4) — model must output `<lm>` itself, forcing full tag structure.
- **ExtractCleanResponse:** Extracts content between `<lm>` and `</lm>`, then parses inner tags. Off-by-one fixes for closing tag lengths (v10.14.2: `</output>` = 9 chars, `</toolcall>` = 11 chars).
- **History rendering:** Past assistant responses wrapped as `<assistant><lm>...</lm></assistant>`.
- **Tag renamed from `<llm>` to `<lm>` (v10.15.2):** eliminated double-l hallucination where model wrote `<\lllm>` instead of `</llm>`.
- **No TrimToFirstClosingTag:** Removed. ExtractCleanResponse handles everything.
- **Future parallelism ready:** `</toolcall>` is NOT a stop tag. Multiple toolcalls can exist inside one `<lm>` container.

## 🚨 CRITICAL LESSONS

### 1. StatelessExecutor vs InteractiveExecutor (2026-08-12)
**InteractiveExecutor** = incremental chat (feed once, then new tokens only). KV cache persists.
**StatelessExecutor** = full prompt each call. Fresh context per call. No state.
ECAssistant uses InteractiveExecutor with KV cache prefill (static prefix cached once, incremental feed per turn).
**Never mix** full-prompt-rebuild with a stateful executor — silently produces empty output on turn 2+.

### 2. Turn Counter Reset Between Requests (2026-08-12)
Two separate `_turnCount` fields (orchestrator + engine) must both reset between user requests. `ExecuteMultiStep` calls `Reset()` + `_engine.ResetTurnCount()` at start.

### 3. Generation Cue — Model Must Output `<lm>` Itself (v10.15.4)
Generation cue is `<assistant>` only — NOT `<assistant><lm>`. The model must generate the full `<lm>...</lm>` structure itself. With `<assistant><lm>` as cue, the model started after `<lm>` and never output the opening tag.

### 4. Anti-Prompts — Safe vs Dangerous (2026-08-12)
**Safe:** `User:`, `\n```\n`, `Question:`, `### User`, `<user>` — don't appear in prompt format.
**Never use:** `---`, `</toolcall>`, `</output>`, `</lm>` — appear in SystemPrompt and history. LLamaSharp's built-in anti-prompt system can match them in prompt context and stop generation before it starts.

### 5. ESC Stop Must Bail Out Immediately (v10.11.1)
When ESC is pressed during generation, `GenerateAsync` returns `"(Stopped by user)"`. The orchestrator must detect this and return immediately — NOT attempt format retries. After ESC: clear context window, rebuild KV cache, reset for next command.

### 6. Shell Error Handling (v10.11.2 + v10.12.10)
**Windows/PowerShell:** `$ErrorActionPreference = 'Continue'` — all commands run, errors collected at end. Never use `'Stop'` — kills script at first error, remaining `;`-separated commands never execute.
**Mac/zsh:** `#!/bin/zsh` shebang, commands run directly. zsh exits on first error by default — semicolon-chained commands may not all run if one fails.

### 7. `<lm>` Container — No Fallback Stop Tags (v10.12.15)
Only `</lm>` is a stop tag. `</output>` as a fallback could cause early stops if the model writes `</output>` inside content (code examples, HTML).

### 8. Off-By-One in Closing Tag Lengths (v10.14.2)
`ExtractCleanResponse` used `oc + 8` for `</output>` (9 chars) and `tcEnd + 10` for `</toolcall>` (11 chars), truncating the final `>` from both. This caused `ParseLLMDecision` to fail matching, triggering endless format retry loops. Always use `.Length` or count carefully.

### 9. KV Cache Rewind — Hybrid Approach (v10.15)
Format retry: fast `LoadState` rewind as default (state saved before `InferAsync` feeds incremental input). Full `ResetAndRebuildCacheAsync` as fallback if `LoadState` fails 2x consecutively. After rebuild, re-feed conversation history from context window into fresh KV cache.

### 10. Sub-Task Advancement — Guard Against Infinite Loops (v10.15.8)
`AdvanceSubTask` is a no-op when `_subTasks.Count <= 1`. A `while` loop calling it without a `Count > 1` guard infinite-loops on single-step tasks. Always match the guard condition.

### 11. Single Toolcall Can Cover Multiple Steps (v10.15.7)
One shell command with semicolons can create 3 files, covering 3 planned steps. The single-tool success path must advance ALL remaining sub-tasks, not just one. But guard with `Count > 1` to avoid the infinite loop.

### 12. Tool Failure Must Be Fed Back to LLM (v10.15.1)
On single-tool failure: log to `_toolCallLog` (for streak detection), feed error via `AddToolResult`, inject `BuildStepDirective`. Without this, the LLM never knows the tool failed and loops with the same context.

### 13. Cross-Platform Shell Execution (v10.16)
`EShellAgent` detects OS at runtime: Windows uses `powershell.exe` with `.ps1` temp scripts, macOS uses `/bin/zsh` with `.sh` temp scripts. Tool examples and rules are OS-aware. System prompts are OS-specific (SystemPrompt.Windows.md / SystemPrompt.Mac.md).

## Architecture Overview

```
Program.cs (entry point, CLI loop, startup, tool registration)
    │
    ├── Startup Flow:
    │   1. Create ~/ECAssistant/ if missing
    │   2. Copy appsettings.json from build dir if missing (first run only)
    │   3. Load config from ~/ECAssistant/appsettings.json
    │   4. Resolve model path (absolute path from config)
    │   5. Load OS-specific system prompt (Windows/Mac/fallback)
    │   6. Create EAgentEngine (model, 16384 context, 15 GPU layers, working dir)
    │   7. Load secondary model (Phi-4-mini, configurable sampling params + anti-prompts)
    │   8. WireSummaryService, InitializeVectorMemory, SelfCorrection, ProjectContext
    │   9. InitializeTaskPlanner (task decomposition)
    │   10. Register 7 tools (self-inject rules into system prompt, OS-aware)
    │   11. Start RunAgentLoop
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
        │   │   │   ├── Manual stop tag check: only </lm>
        │   │   │   ├── ESC key detection → bail out
        │   │   │   └── 90s timeout, 2048 max tokens
        │   │   └── ExtractCleanResponse: extract <lm>...</lm> → parse inner tags
        │   │
        │   ├── 2. ESC stop check → if stopped: clear context, rebuild cache, return
        │   ├── 3. ParseLLMDecision: toolcall | output | invalid
        │   │
        │   ├── 4a. Tool call (single or multi):
        │   │   ├── Single: execute → advance ALL sub-tasks on success
        │   │   ├── Multi: ParallelToolExecutor → dependency analysis → Task.WhenAll
        │   │   ├── On success: advance sub-tasks, AddToolResult, InjectFormatRetry
        │   │   ├── On failure: log, feed error back, inject directive (v10.15.1)
        │   │   └── Continue loop
        │   │
        │   ├── 4b. Direct answer (<output>):
        │   │   └── Return OrchestratorResult (goal achieved)
        │   │
        │   └── 4c. Invalid (no tags):
        │       ├── RemoveLastAssistantResponseAsync (hybrid: rewind or full rebuild)
        │       ├── InjectFormatRetry (with <lm> wrapper example)
        │       ├── Retry up to 2 times
        │       └── After 2 failures → stop with error
```

## Tool System (7 Tools)

```
EToolBase (abstract) — Name, Description, UsageExample, GetToolRules(), GetToolExample()
├── ToSystemPromptBlock() — assembles all into system prompt at runtime (OS-aware for EShellAgent)

1. EShellAgent       — file ops, shell commands, 60s timeout (PowerShell on Windows, zsh on Mac)
2. EFileResearchTool — project-wide file scan
3. EBackgroundExec   — background process management
4. EWebSearch        — DuckDuckGo web search (no auth)
5. EDotnetBuild      — build/test/test-filter/format with structured error parsing
6. EGitTool          — git operations with structured output
7. ECodeEditor       — surgical code editing: patch/diff/search/replace-all/insert/delete-lines

ToolPolicy: 3 levels (Allowed / ApprovalRequired / Blocked) checked before every execution
```

## Cross-Platform Support (v10.16)

### EShellAgent OS Detection
- `OperatingSystem.IsWindows()` → `powershell.exe` with `.ps1` temp scripts
- `OperatingSystem.IsMacOS()` → `/bin/zsh` with `.sh` temp scripts
- UsageExample, GetToolRules, GetToolExample all OS-aware

### Dual System Prompts
- `SystemPrompt.Windows.md` — PowerShell cmdlet examples (Get-Content, Set-Content, etc.)
- `SystemPrompt.Mac.md` — zsh equivalents (cat, echo >, cp, grep, sed, etc.)
- `SystemPrompt.md` — legacy fallback
- Engine loads correct file based on OS, falls back to SystemPrompt.md

### .csproj
- `TargetFramework`: `net8.0` (was `net8.0-windows`)
- GPU backends via MSBuild conditionals:
  - Cuda12: Windows only (NVIDIA GPU)
  - Vulkan: All platforms (Metal/MoltenVK on Mac, Vulkan drivers on Windows/Linux)
  - Cpu: Always included (safe fallback)

### Models (gitignored, stored at ~/Agent/models/)
- `Qwen_Qwen3-8B-Q4_K_M.gguf` (4.7 GiB) — primary model (bartowski)
- `microsoft_Phi-4-mini-instruct-Q4_K_M.gguf` (2.3 GiB) — secondary model (bartowski)

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
- Auto-scan: on startup, scans all .cs/.md/.json/.txt/.xml/.sql/.html/.css/.js files
- Dependency graph from `using` statements
- Impact analysis: "changing file X may affect files Y, Z"
- Prompt injection: file list + dependencies (code tasks only — keyword gated)

### Task Decomposition (TaskPlanner)
- LLM-based decomposition (secondary model) with keyword fallback
- Progress tracking: checklist with status markers
- Step-aware directives with [TASK PROGRESS] and completion check
- Sub-task advancement: single toolcall advances ALL remaining steps on success (v10.15.7)
- Guard: `Count > 1` check prevents infinite loop on single-step tasks (v10.15.8)

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
- **RemoveLastAssistantResponseAsync:** hybrid approach (v10.15)
  - Default: fast `LoadState` rewind to pre-generation KV cache state
  - Fallback: full `ResetAndRebuildCacheAsync` + re-feed conversation history (after 2 failures)
- **SummaryService:** LLM-based compaction (uses engine's own model)
- **Transcript:** auto-saved on every tool call (crash recovery), JSON format
- **KV cache overflow:** at 80%, summarize with secondary model, rebuild cache, re-inject
- **KV cache rebuild after ESC:** RebuildCacheAfterStopAsync re-prefills static prefix fresh

## Secondary Model (v10.12.12+)

Fully configurable via `appsettings.json`:
```json
"secondary_model": {
    "enabled": true,
    "model_path": "/Users/localdev/Agent/models/microsoft_Phi-4-mini-instruct-Q4_K_M.gguf",
    "context_size": 4096,
    "gpu_layers": 0,
    "temperature": 0.1,
    "top_p": 0.8,
    "top_k": 40,
    "repeat_penalty": 1.1,
    "max_tokens": 512,
    "anti_prompts": ["User:", "Question:", "Assistant:", "###", "<user>", "<tooloutput>"]
}
```

## Console Output (v10.12.20)

Centralized truncation in `EGuiBase`:
- `EGuiBase.Truncate(text, maxChars)` — static, returns truncated + `[...]` string
- All call sites use `EGuiBase.Truncate()` — one place to change behavior

## Design Decisions

1. **Shell as primary tool** — no separate file op classes (was PowerShell, now OS-aware EShellAgent)
2. **Tool self-registration** — SystemPrompt is tool-agnostic (OS-specific prompts have no tool rules)
3. **Dual memory** — keyword + TF-IDF vector, no external deps
4. **`<lm>` container (v10.12)** — noise-proof response parsing, one stop tag, no fallbacks
5. **`<lm>` not `<llm>` (v10.15.2)** — eliminates double-l hallucination
6. **Generation cue `<assistant>` only (v10.15.4)** — model outputs `<lm>` itself
7. **ExtractCleanResponse** — single extraction point, correct tag lengths (v10.14.2)
8. **Format retry with hybrid rewind (v10.15)** — fast LoadState default, full rebuild fallback
9. **Tool failure fed back to LLM (v10.15.1)** — log + AddToolResult + InjectFormatRetry
10. **Single toolcall advances all sub-tasks (v10.15.7)** — but guarded by Count > 1 (v10.15.8)
11. **Cross-platform (v10.16)** — net8.0, OS-aware shell, dual system prompts, conditional backends
12. **ESC stop bails out immediately** — no format retries, clear context + rebuild cache
13. **InteractiveExecutor with KV cache** — static prefix prefilled once, incremental feed per turn
14. **Parallel multi-tool execution (v10.13)** — multiple toolcalls per `<lm>`, dependency analysis, Task.WhenAll
15. **Models outside repo** — `~/Agent/models/`, gitignored, absolute paths in config

## 🔖 Known-Good Builds (Git Tags)

| Tag | Version | Description |
|-----|---------|-------------|
| `mac-safe` | v10.16.0 | Cross-platform, EShellAgent, dual prompts, Mac-tested |
| `pre-mac` | v10.15.8 | Pre-migration checkpoint (Windows-only, EPowerShellAgent) |
| `v10.12.20-working` | v10.12.20 | Centralized truncation, audited clean |

**If any change breaks multi-turn:**
```bash
git checkout mac-safe       # Current (cross-platform, Mac-tested)
git checkout pre-mac        # Pre-migration (Windows-only)
git checkout v10.12.20-working  # Pre-<lm> audit baseline
```

**Status:** v10.16.0 — Cross-platform (Windows + macOS), EShellAgent, dual system prompts, all v10.15.x fixes. Tested on Mac with Qwen3-8B. Ready for production testing on both platforms.