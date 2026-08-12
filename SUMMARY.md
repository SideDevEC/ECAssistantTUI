# ECAssistant — Project Summary (v10.12.20 — 2026-08-12)

**Summary:** A local, offline AI agent built in C# .NET 8 using LLamaSharp. Loads GGUF models from disk — no API calls, no cloud, fully self-contained. Uses `<llm>` container tag for noise-proof response parsing with XML-style inner tags for tool calling. Multi-step autonomous loops, dual memory (keyword + vector/semantic), sliding context windows, self-correction with failure loop detection, project context awareness, task decomposition, surgical code editing, 7 registered tools. Secondary model (Phi-4-mini) with fully configurable sampling params and anti-prompts. Centralized console truncation with `[...]` indicators.

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0-windows`, Nullable enabled)
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk
- **Runtime:** Self-hosted, offline inference — no external API calls
- **Default Model:** Qwen3-8B-Q4_K_M (configurable via appsettings.json)
- **Repo:** `github.com/LLamaDudeX/ECAssistant.git` (branch: `main`)
- **Latest Tag:** `v10.12.20-working`
- **Package deps:** LLamaSharp 0.27.0 + backends (Vulkan/Cuda12/CPU), Microsoft.Extensions.Logging.Abstractions
- **Executor:** InteractiveExecutor with KV cache (static prefix prefilled once, incremental feed per turn)
- **Secondary Executor:** StatelessExecutor (fresh context per call, no cache)
- **Secondary Model:** Phi-4-mini-instruct-Q4_K_M (task decomposition + summarization)
- **Source files:** 34 .cs files, SystemPrompt.md v6 (~1300 tokens)

## What's New (v10.11-v10.12 — Session 2026-08-12)

### `<llm>` Container Tag System (v10.12)
- All model responses wrapped in `<llm>...</llm>` — noise outside is ignored
- Only `</llm>` is a streaming stop tag — no fallbacks, clean and predictable
- `ExtractCleanResponse` extracts content from `<llm>`, then parses inner tags
- `TrimToFirstClosingTag` removed — no double-trimming
- Generation cues use `<assistant><llm>` to force container opening
- History rendering wraps past responses as `<assistant><llm>...</llm></assistant>`
- All 7 directives show full structural example
- Future parallelism ready: `</toolcall>` is NOT a stop tag

### ESC Stop Fix (v10.11.1)
- ESC during generation no longer causes "invalid input" on next command
- Orchestrator detects stop state, bails out immediately (no format retries)
- Context window cleared, KV cache rebuilt, ready for next command

### PowerShell Error Handling (v10.11.2 + v10.12.10)
- Non-terminating errors (e.g. bad path) no longer reported as success
- `$ErrorActionPreference = 'Continue'` — all commands run, errors collected at end
- try/catch wrapper catches errors, reports via Write-Error, exits with code 1
- stderr checked even if exit code is 0 — [PS Warning] with error text

### Secondary Model Configurable (v10.12.12-v10.12.14)
- Sampling params (temperature, top_p, top_k, repeat_penalty, max_tokens) in appsettings.json
- Anti-prompts configurable — stops drifting (User:, Question:, Assistant:, ###, etc.)
- All derived values relative to config (SummarizeAsync 25%, DecomposeTaskAsync 50%, convText 75%)
- Defaults lowered: temp 0.3→0.1, top_p 0.9→0.8 (reduce drifting)

### Centralized Truncation (v10.12.19-v10.12.20)
- `EGuiBase.Truncate(text, maxChars)` — one static method, returns truncated + `[...]`
- All 9 console output sites use it — one place to change behavior
- `[...]` indicator shows when content is truncated in console

## Working Directory

Everything lives in `~/ECAssistant/`:

```
~/ECAssistant/
├── appsettings.json          ← User config (editable)
├── ECAssistant.log            ← Structured log file
├── transcript.json            ← Conversation transcript (auto-saved)
├── SystemPrompt.md            ← System prompt (fallback to build dir)
├── .project_context.json      ← Project scan results
├── .snapshots/                ← File rollback snapshots
├── Memory/                    ← Keyword memory entries (JSON)
├── Workspace/                 ← Agent workspace files
├── vecmem/                    ← Vector memory store
└── <model>.gguf               ← Model file (user provides)
```

## Registered Tools (7)

| Tool | Purpose | Key Feature |
|------|---------|-------------|
| **EPowerShellAgent** | File/system ops, any shell command | Continue+try/catch, 60s timeout |
| **EFileResearchTool** | Project-wide file scan | Multi-file content scan |
| **EBackgroundExec** | Background process management | start/status/output/kill |
| **EWebSearch** | Web search | DuckDuckGo API, no auth |
| **EDotnetBuild** | .NET build/test/format | Structured errors, test-filter |
| **EGitTool** | Git operations | status/diff/commit/push/pull/log |
| **ECodeEditor** | Surgical code editing | patch/diff/search/replace/insert/delete |

## Agentic Capabilities

| Capability | How |
|-----------|-----|
| **Self-correction** | Failure loop detection (3x → escalate), alternating patterns, file snapshots/rollback |
| **Project understanding** | Auto-scan, dependency graph, impact analysis, prompt injection (code tasks only) |
| **Task decomposition** | LLM-based (secondary model) with keyword fallback, sub-task progress tracking |
| **Surgical code editing** | Multi-line patch with uniqueness check, diff preview, cross-file search & replace |
| **ESC stop recovery** | Detect stop, clear context, rebuild KV cache, ready for next command |
| **KV cache management** | Prefill static prefix, incremental feed, overflow summarize+rebuild, rewind on retry |

## Configuration (appsettings.json)

```json
{
  "llm": {
    "model_path": "Qwen3-8B-Q4_K_M.gguf",
    "context_size": 16384,
    "gpu_layers": 15
  },
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
    "anti_prompts": ["User:", "Question:", "Assistant:", "###", "<user>", "<tooloutput>"]
  },
  "inference": {
    "max_tokens": 2048,
    "temperature": 0.3,
    "anti_prompts": ["User:", "\n```\n", "Question:", "### User", "<user>"]
  },
  "context_management": {
    "strategy": "SummaryAndShift",
    "keep_last": 20,
    "max_summary_length": 2000
  }
}
```

## Response Format (v10.12 — `<llm>` Container)

```
Tool call:
<llm><thinking>Brief reasoning</thinking><toolcall>ToolName<argname>value</argname></toolcall></llm>

Direct answer:
<llm><thinking>Brief reasoning</thinking><output>Answer to user</output></llm>
```

Rules:
1. FIRST token is always `<llm>`. LAST token is always `</llm>`.
2. Inside: ONE `<thinking>`, then ONE `<toolcall>` OR ONE `<output>`. Then `</llm>`. Then STOP.
3. Never write text outside `<llm>...</llm>`.
4. Can batch multiple PowerShell commands with `;` in one toolcall.

## Orchestration Flow

```
ExecuteMultiStep(goal):
  Loop (max 5 turns):
    1. GenerateAsync: BuildIncrementalInput → stream tokens → stop at </llm> → ExtractCleanResponse
    2. ESC check → if stopped: clear context, rebuild cache, return
    3. ParseLLMDecision: toolcall | output | invalid
    4a. Tool call → policy check → execute → AddToolResult → step directive → loop
    4b. Direct answer → return to user
    4c. Invalid → remove bad response → format retry (with <llm> example) → retry max 2
```

## Project Tree

```
ECAssistant/
├── ECAssistant.csproj               ← .NET 8 project
├── SystemPrompt.md                  ← v6: <llm> container rules (~1300 tokens)
├── appsettings.json                  ← Runtime config (with secondary model params)
├── SUMMARY.md / ARCHITECTURE.md
│
├── Program.cs                        ← Entry point: CLI, startup, tool registration
├── Orchestrator.cs                   ← Multi-step loop, ESC stop, step directive
├── EColor.cs                         ← ANSI color helpers
│
├── Engine/
│   ├── EAgentEngine.cs               ← Core LLM engine, <llm> extraction, KV cache
│   ├── ContextWindow.cs               ← Sliding window, auto-summarize
│   ├── ConversationTranscript.cs      ← JSON transcript persistence
│   ├── TokenCounter.cs                ← LLamaSharp tokenizer counting
│   ├── SecondaryModelLoader.cs        ← Configurable secondary model (sampling + anti-prompts)
│   ├── SummaryService.cs              ← LLM-based context summarization
│   ├── SelfCorrectionManager.cs       ← Failure loop detection, snapshots, rollback
│   ├── ProjectContextManager.cs       ← Project scan, dependency graph
│   └── TaskPlanner.cs                 ← Task decomposition, sub-task tracking
│
├── Tools/
│   ├── EToolBase.cs                   ← Abstract base
│   ├── ToolPolicy.cs                  ← 3-level permission system
│   ├── EPowerShell/EPowerShellAgent.cs ← Continue+try/catch, 60s timeout
│   ├── EResearch/EFileResearchTool.cs
│   ├── EBackground/EBackgroundExecTool.cs
│   ├── EWeb/EWebSearchTool.cs
│   ├── EDotnet/EDotnetBuildTool.cs
│   ├── EGit/EGitTool.cs
│   └── ECode/ECodeEditorTool.cs
│
├── Session/AgentSession.cs
├── Services/
│   ├── BackgroundProcessManager.cs
│   ├── FileWatcherService.cs
│   └── Logger.cs
├── Memory/
│   ├── EMemoryManager.cs
│   └── VectorMemoryStore.cs
├── Config/EAgentConfig.cs             ← Full config model (with secondary sampling params)
├── Analysis/EContextAnalyzer.cs
└── UI/
    ├── EGuiBase.cs                     ← Abstract UI + Truncate() centralized
    └── EGuiConsole.cs                  ← Console implementation
```

## Audit Status (v10.12.20)

- **0 bugs found** ✅
- 10 pre-existing warnings (all non-critical: nullability annotations, unused fields)
- `appsettings.json` valid JSON ✅
- SystemPrompt rules 1-14 sequential, no contradictions ✅
- Stop tags: only `</llm>` ✅
- All 9 truncation sites use `EGuiBase.Truncate()` ✅
- Secondary model: anti-prompts in GenerateAsync, relative limits ✅

## Git Tags (Session 2026-08-12)

```
v10.11.1-working   — ESC stop fix
v10.11.2-working   — PowerShell error handling
v10.12.1-working   — </toolcall> dropped from stop tags
v10.12.5-working   — Full <llm> structural directives
v10.12.6-working   — 3 audit bugs fixed
v10.12.7-working   — Removed TrimToFirstClosingTag
v10.12.10-working  — Batching allowed + Continue mode
v10.12.11-working  — Console display fix
v10.12.13-working  — Relative secondary model limits
v10.12.15-working  — Only </llm> stop tag
v10.12.17-working  — Restored primary anti-prompts
v10.12.20-working  — Centralized truncation (current) ←
```

**Status:** v10.12.20 — Audited clean. `<llm>` container system, ESC fix, PowerShell error handling, configurable secondary model, centralized truncation. Ready for production testing.