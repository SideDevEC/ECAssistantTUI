# ECAssistant Architecture (v10.21.2 — 2026-08-13)

**Summary:** A local, offline AI agent in C# .NET 8 using LLamaSharp. Runs GGUF models locally with no external API calls. Uses `<lm>` container tag for noise-proof response parsing with XML-style inner tags (`<thinking>`, `<toolcall>`, `<output>`). 8 registered tools self-register their rules at runtime. Multi-step autonomous loops with dual memory (keyword + TF-IDF vector), sliding context windows with LLM summarization, self-correction with failure loop detection and file rollback, project context awareness with dependency graph, two-phase task planning (decompose → map → execute), surgical code editing, background process management, file watching, structured logging, and sub-agent system with shared model weights. Token-optimized for 8B models. Secondary model (Phi-4-mini) with fully configurable sampling params and anti-prompts. v10.13: Parallel multi-tool execution. v10.16: Cross-platform (Windows + macOS). v10.17: StepMapper (two-phase planning), automated test framework. v10.18: Sub-agent system. v10.19.2: All artifacts in working directory. v10.20: Fully isolated multi-session architecture — each session has own engine, KV cache, tools, memory, output buffer, and prompt queue. Sessions share one model in RAM with serialized inference. File-based JSONL output buffer with output states (not colors). Session is the UI gateway — all components route output through ISessionOutput. No inter-session communication (tool-level concern for later). v10.21: Shared model weights (one GGUF load via `LLamaWeights` shared across sessions, each gets own `LLamaContext`/KV cache). Terminal.Gui TUI replaces raw console — scrollable output view + always-free input field + status bar. Session discovery from disk with animated loading indicator. Native llama.cpp C++ log redirect to file (clean console).

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0`, cross-platform, Nullable enabled)
- **Platforms:** Windows (PowerShell) + macOS (zsh) — OS detected at runtime
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk
- **Backends:** Cuda12 (Windows/NVIDIA), Vulkan (all platforms/Metal), Cpu (always)
- **Executor:** InteractiveExecutor with KV cache (static prefix prefilled once, incremental feed per turn)
- **Secondary Executor:** StatelessExecutor (for summaries/decomposition, no cache)
- **Primary Model:** Qwen_Qwen3-8B-Q4_K_M (bartowski) — `/Users/localdev/Agent/models/`
- **Secondary Model:** microsoft_Phi-4-mini-instruct-Q4_K_M (bartowski) — same folder
- **Tools:** 8 registered — EShellAgent, EFileResearchTool, EBackgroundExec, EWebSearch, EDotnetBuild, EGitTool, ECodeEditor, ESubAgent
- **Config:** `~/ECAssistant/appsettings.json` (user-editable, bundled as fallback)
- **System Prompts:** SystemPrompt.Windows.md (PowerShell examples) + SystemPrompt.Mac.md (zsh examples) — OS-specific, fallback to SystemPrompt.md
- **Working Directory:** `~/ECAssistant/` (ALL disk writes — logs, tests, temp scripts, sub-agent dirs, background agent dirs — v10.19.2)
- **Context:** 16384 tokens, max_tokens 2048, auto-summarize at 50%
- **Repo:** `github.com/LLamaDudeX/ECAssistant.git` (branch: `main`)
- **UI:** EGuiConsole with ANSI scroll region + session buffer batching (v10.21.2)

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

### 14. All Artifacts in Working Directory (v10.19.2)
Previously: test sandboxes in `~/ECAssistant-Tests/`, temp scripts in `/tmp/`, sub-agent dirs in `/tmp/eca-subagent/`, background agent dirs in `/tmp/eca-bgagent/`, test logs scattered in `/tmp/`.
Now: everything inside `~/ECAssistant/`:
- Test sandboxes → `~/ECAssistant/tests/`
- Test logs → `~/ECAssistant/logs/`
- Shell temp scripts → `~/ECAssistant/.tmp/`
- Sub-agent working dirs → `~/ECAssistant/.subagents/`
- Background agent working dirs → `~/ECAssistant/.bgagents/`
`BackgroundProcessManager` also made OS-aware (was Windows-only `powershell.exe`, now uses `/bin/zsh` on Mac).

### 15. Terminal.Gui TUI Does Not Work for ECAssistant (v10.21 to v10.21.1)
Terminal.Gui v1.9.0 replaced raw console I/O. It caused **distortion and freezing** on macOS Terminal.app. Root causes:
- `TextView.Text = TextView.Text + text` is O(n^2) — full string copy + reassign on every token
- `CursorPosition = new Point(0, len)` used character offset as row — wrong, distorted the view
- `Application.MainLoop.Invoke` per-token flooded the event loop
- `ESC 7`/`ESC 8` (DECSC/DECRC) rendered as literal `3` characters on macOS Terminal.app
**Fix:** Reverted to EGuiConsole with ANSI scroll region. No Terminal.Gui dependency.

### 16. ANSI Scroll Region — Use Session Buffer, Not Per-Token UI Writes (v10.21.2)
The ANSI scroll region (`ESC[top;bottomr`) confines scrolling to rows 1..(H-1), keeping input at row H. But per-token `Console.Write` with cursor repositioning caused:
- **Gaps**: Column tracking broke because ANSI color codes are invisible but counted as visible chars
- **Overwrites**: Repositioning cursor to row start on every WriteRaw overwrote previous tokens
- **Prompt disappears**: `ESC[s`/`ESC[u` single-slot save/restore unreliable — saved position lost
**Fix:** Stop pushing `raw_token` entries to UI. Let `AgentSession._streamBuffer` accumulate tokens. Flush as batch `stream` entries on `WriteLine()` or state change. One `Console.Write` per flush — no cursor repositioning needed.

### 17. EColor Must Route Through Gui (v10.21.2)
`EColor.TagBold()`, `EColor.WriteLine()`, `EColor.Write()` called `Console.WriteLine` directly, bypassing EGuiConsole scroll region cursor management. All startup messages and tool output went to the wrong cursor position.
**Fix:** Added `WriteHandler`/`WriteLineHandler` static delegates to EColor. Set at startup to route through Gui. All EColor output now goes through scroll region.

### 18. ConsoleUiRenderer Must Route Through Gui (v10.21.2)
`ConsoleUiRenderer.OnOutput()` called `Console.Write`/`Console.WriteLine` directly, bypassing EGuiConsole. Session output went to wrong cursor position, garbling the input line.
**Fix:** `ConsoleUiRenderer` now takes `EGuiBase` constructor param. All output calls go through Gui methods.

### 15. Terminal.Gui TUI Doesn't Work for ECAssistant (v10.21→v10.21.1)
Terminal.Gui v1.9.0 was added to replace raw console I/O. It caused **distortion and freezing** on macOS Terminal.app. Root causes:
-  is O(n²) — full string copy + reassign on every token
-  used character offset as row — wrong, distorted the view
-  per-token flooded the event loop
- / (DECSC/DECRC) rendered as literal `3` characters on macOS Terminal.app
**Fix:** Reverted to EGuiConsole with ANSI scroll region. No Terminal.Gui dependency.

### 16. ANSI Scroll Region — Use Session Buffer, Not Per-Token UI Writes (v10.21.2)
The ANSI scroll region () confines scrolling to rows 1..(H-1), keeping the input line at row H. But per-token  with cursor repositioning caused:
- **Gaps**: Column tracking broke because ANSI color codes (`[36m`) are invisible but counted as visible characters
- **Overwrites**: Repositioning cursor to row start on every `WriteRaw` overwrote previous tokens
- **Prompt disappears**: `ESC[s`/`ESC[u` single-slot save/restore unreliable — saved position lost
**Fix:** Stop pushing `raw_token` entries to UI. Let `AgentSession._streamBuffer` accumulate tokens. Flush as batch `stream` entries on `WriteLine()` or state change. One `Console.Write` per flush — no cursor repositioning needed.

### 17. EColor Must Route Through Gui (v10.21.2)
`EColor.TagBold()`, `EColor.WriteLine()`, `EColor.Write()` called `Console.WriteLine` directly, bypassing EGuiConsole scroll region cursor management. All startup messages and tool output went to the wrong cursor position.
**Fix:** Added `WriteHandler`/`WriteLineHandler` static delegates to EColor. Set at startup: `EColor.WriteLineHandler = (s) => Gui.WriteLineColored(s)`. All EColor output now routes through Gui → scroll region.

### 18. ConsoleUiRenderer Must Route Through Gui (v10.21.2)
`ConsoleUiRenderer.OnOutput()` called `Console.Write`/`Console.WriteLine` directly, bypassing EGuiConsole. Session output went to wrong cursor position, garbling the input line.
**Fix:** `ConsoleUiRenderer` now takes `EGuiBase` constructor param. All output calls go through `_gui.WriteRaw()`/`_gui.WriteLineColored()`/`_gui.BlankLine()`.

## Session Architecture (v10.20 + v10.21)

**Fully isolated multi-session system with shared model weights.** Each session is an independent agent with its own engine, context, tools, memory, and output buffer. Sessions share one `LLamaWeights` instance in RAM (one GGUF load) but each gets its own `LLamaContext` (own KV cache).

**Key design principles:**
1. **Session = fully independent agent** — own EAgentEngine (own KV cache), own orchestrator, own tools, own memory
2. **No inter-session communication** — sessions don't know about each other. If needed, it's a tool-level concern (future `ESessionMessage` tool)
3. **Shared model weights, separate KV caches** — one `LLamaWeights` loaded ONCE by `SessionManager`, each session gets own `EAgentEngine` with own `LLamaContext` (KV cache). `_sharesWeights` flag prevents disposing shared weights. Inference serialized via `SemaphoreSlim(1,1)`
4. **Session is the UI gateway** — all components (orchestrator, engine, tools) get `ISessionOutput` reference and call `session.Write/WriteLine/WriteRaw` for output
5. **Output states, not colors** — session emits semantic states (Info/Success/Warning/Error/Dim/Bold/Raw/System). UI maps states to colors
6. **File-based output buffer** — JSONL file per session (`~/.sessions/<key>/ui_output.jsonl`), append-only, persistent, scrollable
7. **Auto-flushing StringBuilder** — token streaming accumulates in memory, state change or WriteLine triggers flush to file
8. **Prompt queue** — if session is running, prompts queue; after execution, next queued prompt starts automatically
9. **Per-session stop** — `session.Stop()` cancels only that session. Other sessions keep running
10. **UI attach/detach** — UI registers as `IUiRenderer` on the active session, gets live notifications. Switching = detach + render full history + attach

**Session files:**
```
~/ECAssistant/.sessions/
├── main/
│   ├── ui_output.jsonl      ← output buffer (JSONL, append-only)
│   └── transcript.json      ← conversation transcript
├── watcher/
│   ├── ui_output.jsonl
│   └── transcript.json
```

**UI commands:**
- `sessions` — list all sessions with status + queue count
- `session <n>` — switch to session n (render full history + live output)
- `session-new <name>` — create a new session
- `session-stop <n>` — stop session n's execution (queue preserved)
- `session-close <n>` — close and delete session n
- `session-peek <n>` — quick glance at last 5 output lines
- `session-queue` — show active session's prompt queue
- `session-queue-remove <i>` — remove prompt i from queue
- `session-queue-clear` — clear active session's queue
- `stop` — stop active session's execution
- `quit`/`exit` — stop all sessions gracefully, save, exit

**See `SESSIONS_DESIGN.md` for the full design document.**

## Architecture Overview

```
Program.cs (entry point, CLI loop, startup, tool registration)
    │
    ├── Startup Flow (v10.21):
    │   1. Create ~/ECAssistant/ if missing
    │   2. Copy appsettings.json from build dir if missing (first run only)
    │   3. Load config from ~/ECAssistant/appsettings.json
    │   4. Init Terminal.Gui TUI (EGuiTerminal — TextView + TextField + StatusBar)
    │   5. Resolve model path (absolute path from config)
    │   6. Create SessionManager (loads LLamaWeights ONCE, redirects native logs to file)
    │   7. Discover sessions from .sessions/ dir (SessionDiscovery)
    │   8. Animated loading (. .. ...) while initializing each session:
    │      ├── Attach TerminalGuiRenderer to session
    │      ├── Init vector memory, project context
    │      ├── Register 7 tools + ESubAgent
    │      └── Load secondary model (Phi-4-mini)
    │   9. [Ready] message, input field focused, user can type
    │   10. On input Enter → ProcessInputAsync (command or session.Prompt)
    │
    ├── Input Flow (v10.21 — Terminal.Gui event loop):
    │   ├── TextField.KeyPress(Enter) → OnInputSubmitted callback
    │   ├── ProcessInputAsync: parse command or route to session.Prompt
    │   ├── session.Prompt → background Task runner → token stream to output view
    │   └── Application.Run() event loop (never blocks, never deadlocks)
    │
    └── AgentOrchestrator.ExecuteMultiStep(goal)
        │
        ├── Loop (max 5 turns):
        │   │
        │   ├── 1. EAgentEngine.GenerateAsync(goal)
        │   │   ├── BuildIncrementalInput (turn 1: user msg + directive + cue)
        │   │   │                       (turn 2+: tool output + directive + cue)
        │   │   ├── Inference: LLamaSharp InteractiveExecutor
        │   │   │   ├── Token streaming to output view via MainLoop.Invoke (real-time)
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

## Tool System (8 Tools)

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
8. ESubAgent         — spawn isolated child agents with shared model weights (v10.18)

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
- Sub-task advancement: based on ExecutionPlan's CoversSubTasks (v10.17)
- Guard: `Count > 1` check prevents infinite loop on single-step tasks

### Two-Phase Planning (v10.17 — StepMapper)

**Pipeline:**
```
1. Secondary model (Phi-4-mini) → decompose goal into text steps ("what to do")
2. Main LLM (Qwen3-8B, stateless) → map steps to concrete tool calls ("how to do it")
3. Execution plan injected into context as system message
4. LLM executes following the plan
5. Sub-task advancement uses plan's CoversSubTasks (no heuristics)
```

**Why main LLM for mapping?**
- Already has tool definitions in KV cache — no duplicate injection
- Better reasoning for tool selection and batching (8B vs 3.8B)
- Plan in context helps LLM stay on track (feature, not noise)
- Secondary model reserved for quick isolated tasks (decomposition, summarization)

**Plan format:**
```xml
<plan>
  <call>
    <tool>EShellAgent</tool>
    <command>echo 1 > file1.txt; echo 2 > file2.txt</command>
    <covers>1,2</covers>
    <desc>Create files 1 and 2</desc>
  </call>
</plan>
```

**Sub-task advancement:** Each `PlannedToolCall` has a `CoversSubTasks` list (0-based indices). When a tool succeeds, the orchestrator advances exactly the sub-tasks the plan says that call covers. Tool-agnostic, no string heuristics.

**Fallback:** If mapping fails, LLM plans ad-hoc (conservative one-step advancement).

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

## Console Output (v10.20 — Session-based)

**v10.20: Output routed through ISessionOutput.**

All components (orchestrator, engine, tools) receive an `ISessionOutput` reference and call `Write/WriteLine/WriteRaw/BlankLine/WriteInfo/WriteSuccess/WriteWarning/WriteError/WriteDim` on it. The session implements `ISessionOutput` and:
1. Writes to a JSONL file (append-only, persistent)
2. Notifies attached `IUiRenderer` (console/canvas) with live updates

**Output states (not colors):** Info, Success, Warning, Error, Dim, Bold, Raw, System. The UI maps states to ANSI colors (console) or CSS (canvas).

**Auto-flushing StringBuilder:** Token streaming (`WriteRaw`) accumulates in memory. State change or `WriteLine` triggers flush to JSONL file as a single `stream` entry.

**ConsoleUiRenderer:** Console `IUiRenderer` implementation with state→ANSI color mapping. `RenderHistory()` reads JSONL file and renders full scrollable history when switching sessions.

**Legacy:** `EGuiBase.Truncate(text, maxChars)` — static, still used for truncation. `Program.Gui` still used for `PromptRaw` (user input) and `LogInternal` (diagnostics).

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
16. **All artifacts in working dir (v10.19.2)** — no leaks to `/tmp/` or `~/ECAssistant-Tests/`
17. **Sub-agents share model weights (v10.18)** — isolated engines, same GGUF on disk, configurable context size
18. **Background agents removed (v10.19.3)** — replaced by session architecture design (see SESSIONS_DESIGN.md)
19. **StepMapper file creation guidance (v10.19.4)** — prefer ECodeEditor for file creation (cross-platform safe), validate method-call syntax in shell commands
20. **Independent subagent config (v10.19.5)** — sub-agents have their own config section with enabled flag, context_size, gpu_layers, threads, and all operational parameters
21. **Shared LLamaWeights (v10.21)** — SessionManager loads GGUF once, passes shared weights to each session's EAgentEngine. Each engine creates own LLamaContext (KV cache) from shared weights. `_sharesWeights` flag prevents disposing shared weights in `DisposeAsync`
22. **Terminal.Gui TUI (v10.21)** — replaces raw Console.Write/ReadLine with Terminal.Gui event loop. Scrollable output view (TextView) + always-free input field (TextField) + status bar. Thread-safe output via `Application.MainLoop.Invoke`. No console sync deadlocks
23. **Session discovery from disk (v10.21)** — SessionDiscovery scans `.sessions/` dir, finds most recently modified session, auto-activates. Animated loading indicator (500ms dots) during init. Legacy transcript.json migrated to `.sessions/main/`
24. **Native log redirect (v10.21)** — `NativeLogConfig.llama_log_set` redirects all C++ llama.cpp output (load_tensors, repack, ggml_metal) to log file, keeping console clean. LLAMA-tagged Logger entries skip console output
25. **ANSI scroll region for free input (v10.21.2)** — `ESC[top;bottomr` sets scroll region rows 1..(H-1), input line at row H. User can type while LLM streams. No Terminal.Gui — raw ANSI only. Windows ANSI enabled via `ENABLE_VIRTUAL_TERMINAL_PROCESSING`.
26. **Session buffer for token batching (v10.21.2)** — `AgentSession.WriteRaw()` appends to `_streamBuffer` without pushing to UI. `FlushBuffer()` on `WriteLine()`/state change delivers batch `stream` entries. One `Console.Write` per batch. Tradeoff: no real-time per-token streaming (can add timer flush later).
27. **EColor routing through Gui (v10.21.2)** — `EColor.WriteHandler`/`WriteLineHandler` delegates route all EColor output through EGuiConsole. Prevents startup messages from bypassing scroll region.
28. **ConsoleUiRenderer through Gui (v10.21.2)** — ConsoleUiRenderer takes `EGuiBase` constructor param, routes all output through Gui methods. Prevents session output from bypassing scroll region.

## 🔖 Known-Good Builds (Git Tags)

| Tag | Version | Description |
|-----|---------|-------------|
|  | v10.21.2 | ANSI scroll region + session buffer batching — clean UI, free input line ← CURRENT |
| _(uncommitted)_ | v10.21 | Shared weights, Terminal.Gui TUI, session discovery, native log redirect |
| `v10.19.5-safe` | v10.19.5 | Independent subagent config section with enabled flag |
| `v10.19.4-safe` | v10.19.4 | StepMapper file creation guidance, sub-agent config inheritance fix |
| `v10.19.3-safe` | v10.19.3 | Removed background agents, kept sub-agents, session design |
| `v10.19.2-safe` | v10.19.2 | All artifacts in working dir, BackgroundProcessManager OS-aware |
| `mac-safe` | v10.16.0 | Cross-platform, EShellAgent, dual prompts, Mac-tested |
| `pre-mac` | v10.15.8 | Pre-migration checkpoint (Windows-only, EPowerShellAgent) |
| `v10.12.20-working` | v10.12.20 | Centralized truncation, audited clean |

**If any change breaks multi-turn:**
```bash
git checkout v10.19.5-safe  # Current (independent subagent config)
git checkout v10.19.4-safe  # StepMapper fixes
git checkout v10.19.3-safe  # Background agents removed
git checkout pre-mac        # Pre-migration (Windows-only)
git checkout v10.12.20-working  # Pre-<lm> audit baseline
```

**Status:** v10.21 — Shared model weights (one GGUF load in RAM), Terminal.Gui TUI with scrollable output + always-free input field, session discovery from disk with animated loading, native llama.cpp log redirect to file (clean console). Build: 0 errors. Core commands (quit, help, tools, stop, clear-history, save-context, free-text prompts) functional. Less common commands trimmed (can be re-added). Token streaming real-time via MainLoop.Invoke. Cross-platform (Windows + macOS).