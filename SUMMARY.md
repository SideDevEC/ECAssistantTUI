# ECAssistant — Project Summary (v10.4.2 — 2026-08-12)

**Summary:** A local, offline AI agent built in C# .NET 8 using LLamaSharp. Loads GGUF models from disk — no API calls, no cloud, fully self-contained. Uses XML-style tags for tool calling with multi-step autonomous loops, dual memory systems (keyword + vector/semantic), sliding context windows, self-correction with failure loop detection, project context awareness, task decomposition, surgical code editing, structured logging, background process management, and 7 registered tools. PowerShell is the primary tool for all file/system operations.

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0-windows`, Nullable enabled)
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk
- **Runtime:** Self-hosted, offline inference — no external API calls
- **Default Model:** Qwen3-8B-Q4_K_M (configurable via appsettings.json)
- **Repo:** `github.com/LLamaDudeX/ECAssistant.git` (branch: `main`)
- **Latest Commit:** `369708a` (v10.4.2)
- **Package deps:** LLamaSharp 0.27.0 + backends (Vulkan/Cuda12/CPU), Microsoft.Extensions.Logging.Abstractions
- **Executor:** StatelessExecutor (fresh context per call — NOT InteractiveExecutor which caches KV state between calls and breaks full-prompt-rebuild architecture)
- **Source files:** 34 .cs files, SystemPrompt.md (5211 chars ~1300 tokens)

## Working Directory

Everything lives in `~/ECAssistant/`. Auto-created on first run, default config copied from build output. All disk writes go here:

```
~/ECAssistant/
├── appsettings.json          ← User config (editable)
├── ECAssistant.log            ← Structured log file
├── transcript.json            ← Conversation transcript (auto-saved per tool call)
├── SystemPrompt.md            ← System prompt (fallback to build dir)
├── .project_context.json      ← Project scan results (persisted)
├── .snapshots/                ← File rollback snapshots (self-correction)
├── Memory/                    ← Keyword memory entries (JSON)
├── Workspace/                 ← Agent workspace files
├── vecmem/                    ← Vector memory store (vectors.json)
└── <model>.gguf               ← Model file (user provides)
```

Build directory is **read-only** — only fallback for config/SystemPrompt/model on first run.

## Design Philosophy

- **PowerShell as primary tool:** No separate C# file op classes. Every LLM knows PowerShell.
- **Tools self-register at runtime:** SystemPrompt.md is tool-agnostic. Each tool injects its own rules via `ToSystemPromptBlock()`.
- **No external deps for memory:** TF-IDF vector search (256-dim, cosine similarity). No FAISS, no Python.
- **Token-efficient:** System prompt + tools = ~2825 tokens (down from ~3700). Optimized for 8B models.
- **StatelessExecutor (v10.4.2):** Critical — full-prompt-rebuild architecture requires stateless executor. InteractiveExecutor's KV cache persistence causes zero token output on turn 2+. See ARCHITECTURE.md for full lesson.
- **Self-correction:** Failure loop detection, file snapshots/rollback, escalation after 3 repeated failures.
- **Project awareness:** Auto-scans project on startup, injects file list + dependency graph for code tasks.

## Registered Tools (7)

| Tool | Purpose | Key Feature |
|------|---------|-------------|
| **EPowerShellAgent** | File/system ops, any shell command | 60s timeout, temp .ps1 scripts |
| **EFileResearchTool** | Project-wide file scan | Multi-file content scan |
| **EBackgroundExec** | Background process management | start/status/output/kill |
| **EWebSearch** | Web search | DuckDuckGo API, no auth |
| **EDotnetBuild** | .NET build/test/format | Structured errors, test-filter, format |
| **EGitTool** | Git operations | status/diff/commit/push/pull/log |
| **ECodeEditor** | Surgical code editing | patch/diff/search/replace-all/insert/delete-lines |

## Agentic Capabilities (v10)

| Capability | How |
|-----------|-----|
| **Self-correction** | `SelfCorrectionManager` — detects repeated errors (3x → escalate), alternating patterns, file snapshots before edit, rollback on failure |
| **Project understanding** | `ProjectContextManager` — auto-scans project, builds dependency graph from imports, injects file list + relationships into prompts (code tasks only) |
| **Task decomposition** | `TaskPlanner` — splits complex requests on "then/and/after that" keywords, tracks sub-task progress with checklist |
| **Surgical code editing** | `ECodeEditor` — multi-line patch with uniqueness check, diff preview, cross-file search & replace, line insert/delete |
| **Failure escalation** | Same error 3x or same tool failing 3x → escalate to user instead of looping |
| **File rollback** | Snapshots before modification, rollback to last snapshot on failure |
| **Impact analysis** | `ProjectContextManager.GetImpactAnalysis()` — shows related files before editing |
| **Auto-format** | `EDotnetBuild action=format` — runs dotnet format after code changes |

## CLI Commands (30+)

| Command | Description |
|---------|-------------|
| `<type request>` | Multi-step agent execution |
| `quit` / `exit` | Exit (saves transcript) |
| `help` | Show help (grouped by category) |
| `tools` | List registered tools with descriptions |
| `clear-history` | Clear conversation history |
| `save-context` | Save transcript to disk |
| `file-pick` | Open file picker, send content to LLM |
| `memory-save` / `memory-query` / `memory-stats` | Keyword memory |
| `vecmem-stats` / `vecmem-search` / `vecmem-add` | Vector/semantic memory |
| `sessions` / `session-status` / `session-create` / `session-cleanup` | Session mgmt |
| `bg-run` / `bg-status` / `bg-output` / `bg-kill` / `bg-cleanup` | Background processes |
| `watch` / `watch-start` / `watch-stop` | File watcher |
| `reload-config` | Reload appsettings.json without restart |
| `swap-model` | Switch GGUF model at runtime |
| `clipboard-read` / `clipboard-write` | Windows clipboard |
| `log` / `log-level` | View logs / set log level |

## Configuration (appsettings.json)

```json
{
  "llm": {
    "model_path": "Qwen3-8B-Q4_K_M.gguf",
    "context_size": 16384,
    "gpu_layers": 15,
    "threads": -1,
    "batch_size": 256,
    "ubatch_size": 128
  },
  "secondary_model": {
    "enabled": false,
    "model_path": "",
    "context_size": 4096,
    "gpu_layers": 0
  },
  "vector_memory": {
    "enabled": true,
    "directory": "vecmem",
    "max_results": 5,
    "auto_index": true
  },
  "inference": {
    "max_tokens": 2048,
    "temperature": 0.3,
    "anti_prompts": ["</toolcall>", "</output>", ...]
  },
  "context_management": {
    "strategy": "SummaryAndShift",
    "keep_last": 20,
    "max_summary_length": 2000
  }
}
```

## System Prompt (SystemPrompt.md v5.1)

Tuned for Qwen3-8B, token-optimized (~1300 tokens):
- Strict response format: `<thinking>` then `<toolcall>` OR `<output>` — no exceptions
- 8 critical rules including "NEVER output text outside tags"
- Compact tool selection guide (7 tools, 2-column table)
- Error handling: fix → rebuild, escalate after 3 failures
- After tool result: MUST respond with `<output>` tags (injected as user message)

## Orchestration Flow

```
ExecuteMultiStep(goal):
  Loop (max 5 turns):
    1. BuildFullPrompt: system prompt + tools + memory + project context + history
    2. GenerateAsync: stream tokens, manual anti-prompt check (break on </toolcall>/<output>)
    3. ExtractCleanResponse: first <thinking> + first <toolcall>/<output> only
    4. TrimToFirstClosingTag
    5. ParseLLMDecision: toolcall | output | invalid
    6a. Tool call → policy check → execute → add result → inject directive as user msg → loop
    6b. Direct answer → return to user
    6c. Invalid → remove bad response from history → inject format error as user msg → retry (max 2)
    6d. Failure escalation: SelfCorrectionManager detects 3x repeated → escalate to user
```

## Project Tree

```
ECAssistant/
├── ECAssistant.csproj               ← .NET 8 project
├── SystemPrompt.md                  ← v5.1: Tuned for Qwen3-8B (~1300 tokens)
├── appsettings.json                  ← Runtime config (bundled, copied to ~/ECAssistant/)
├── SUMMARY.md / ARCHITECTURE.md / GAP_ANALYSIS.md / GAP_FILTERED.md
│
├── Program.cs                        ← Entry point: CLI, startup, tool registration
├── Orchestrator.cs                   ← Multi-step loop, tool policy, format retry, auto-fix
├── EColor.cs                         ← ANSI color helpers
│
├── Engine/
│   ├── EAgentEngine.cs               ← Core LLM engine, prompt building, inference, extraction
│   ├── ContextWindow.cs               ← Sliding window, auto-summarize at 50%, RemoveLastAssistant
│   ├── ConversationTranscript.cs      ← JSON transcript persistence
│   ├── TokenCounter.cs                ← LLamaSharp tokenizer-based counting
│   ├── EDecisionLoop.cs               ← v2: Interactive decision loop
│   ├── SecondaryModelLoader.cs        ← Optional second small model for summarization
│   ├── SummaryService.cs              ← LLM-based context summarization
│   ├── SelfCorrectionManager.cs       ← v10: Failure loop detection, snapshots, rollback
│   ├── ProjectContextManager.cs       ← v10: Project scan, dependency graph, impact analysis
│   └── TaskPlanner.cs                 ← v10: Task decomposition, sub-task tracking
│
├── Tools/
│   ├── EToolBase.cs                   ← Abstract base: Name, Description, Rules, Examples
│   ├── ToolPolicy.cs                  ← 3-level permission system
│   ├── EPowerShell/EPowerShellAgent.cs ← PRIMARY TOOL: file/system ops, 60s timeout
│   ├── EResearch/EFileResearchTool.cs  ← Project-wide file scan
│   ├── EBackground/EBackgroundExecTool.cs ← Background process management
│   ├── EWeb/EWebSearchTool.cs          ← DuckDuckGo web search
│   ├── EDotnet/EDotnetBuildTool.cs     ← Build + test + format with error parsing
│   ├── EGit/EGitTool.cs                ← Git operations with structured output
│   ├── ECode/ECodeEditorTool.cs        ← v10: Surgical code editing (patch/diff/search/replace)
│   └── EExample/EFileAnalyzer.cs       ← Example template for new tools
│
├── Session/AgentSession.cs            ← Session management (main, isolated, named)
├── Services/
│   ├── BackgroundProcessManager.cs    ← Non-blocking process execution
│   ├── FileWatcherService.cs           ← Workspace file monitoring
│   └── Logger.cs                      ← Structured logging (file+console, 4 levels)
├── Memory/
│   ├── EMemoryManager.cs              ← Keyword memory with relevance scoring
│   └── VectorMemoryStore.cs           ← Semantic memory (TF-IDF, cosine similarity)
├── Config/EAgentConfig.cs             ← Full config model
├── Analysis/EContextAnalyzer.cs       ← v2: Project analysis
└── UI/EGuiBase.cs + EGuiConsole.cs    ← Abstract UI + console implementation
```

## Git Workflow

**Repo:** `https://github.com/LLamaDudeX/ECAssistant.git` (branch: `main`)

```bash
cd <project-root>
git add -A && git commit -m "<message>" && git push
```

**Status:** v10.4.2 — 7 tools, self-correction, project context, task planning, token-optimized. Multi-turn workflow fixed (StatelessExecutor).