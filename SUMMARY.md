# ECAssistant — Project Summary (v9.22 — 2026-08-12)

**Summary:** A local, offline AI agent built in C# .NET 8 using LLamaSharp. Loads GGUF models from disk — no API calls, no cloud, fully self-contained. Uses XML-style tags for tool calling with multi-step autonomous loops, persistent memory (keyword + vector/semantic), sliding context windows, structured logging, background process management, and 6 registered tools. PowerShell is the primary tool for all file/system operations.

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0-windows`, Nullable enabled)
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk
- **Runtime:** Self-hosted, offline inference — no external API calls
- **Default Model:** Qwen3-8B-Q4_K_M (configurable via appsettings.json)
- **Repo:** `github.com/LLamaDudeX/ECAssistant.git` (branch: `main`)
- **Latest Commit:** `457a584` (v9.22)
- **Package deps:** LLamaSharp 0.27.0 + backends (Vulkan/Cuda12/CPU), Microsoft.Extensions.Logging.Abstractions

## Working Directory

Everything lives in `~/ECAssistant/` (user home / ECAssistant). The app auto-creates this on first run and copies a default `appsettings.json` from the build output. All disk writes go here:

```
~/ECAssistant/
├── appsettings.json          ← User config (editable)
├── ECAssistant.log            ← Structured log file
├── transcript.json            ← Conversation transcript (auto-saved)
├── SystemPrompt.md            ← System prompt (fallback to build dir)
├── Memory/                    ← Keyword-based memory entries (JSON)
├── Workspace/                 ← Agent workspace files
├── vecmem/                    ← Vector memory store (vectors.json)
└── <model>.gguf               ← Model file (user provides)
```

The build directory is **read-only** — only used as fallback for `appsettings.json`, `SystemPrompt.md`, and model files on first run.

## Design Philosophy

**PowerShell as primary tool:** Instead of separate C# classes for each file operation, the agent uses `EPowerShellAgent` for everything. Every LLM knows PowerShell natively (`Get-Content`, `Copy-Item`, `-replace`, etc.). Keeps the codebase lean and tool surface small.

**Tools self-register at runtime:** `SystemPrompt.md` is tool-agnostic. Each tool class implements `ToSystemPromptBlock()` which injects its own rules, examples, and description into the system prompt at startup. Adding/removing tools requires no SystemPrompt.md edits.

**No external dependencies for memory:** Vector memory uses TF-IDF embeddings (256-dim hash-based, L2 normalized, cosine similarity). No FAISS, no Python, no extra NuGet packages. Ships with the build.

## Registered Tools (6)

| Tool | Purpose | Key Feature |
|------|---------|-------------|
| **EPowerShellAgent** | File/system ops, any shell command | 60s timeout, temp .ps1 scripts, XML escaping |
| **EFileResearchTool** | Project-wide file scan | Multi-file content scan with extension filter |
| **EBackgroundExec** | Background process management | start/status/output/kill — non-blocking |
| **EWebSearch** | Web search | DuckDuckGo API, no auth needed |
| **EDotnetBuild** | .NET build/test with error parsing | Structured errors (file, line, code, message) |
| **EGitTool** | Git operations | status/diff/commit/push/pull/log/branch/checkout |

## CLI Commands

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
| `clipboard-read` / `clipboard-write` | Windows clipboard (Windows only) |
| `log` / `log-level` | View logs / set log level |

## Configuration (appsettings.json)

```json
{
  "llm": {
    "model_path": "Qwen3-8B-Q4_K_M.gguf",  // relative to ~/ECAssistant/ or absolute
    "context_size": 8192,                     // safe default (was 32768, caused OOM)
    "gpu_layers": 15,                          // safe default (was 35, caused memory corrupt)
    "threads": -1,                             // -1 = auto
    "batch_size": 256,
    "ubatch_size": 128
  },
  "secondary_model": {                         // optional, for summarization
    "enabled": false,
    "model_path": "",                          // e.g. Qwen3-1.7B-Q4_K_M.gguf
    "context_size": 4096,
    "gpu_layers": 0                            // CPU only, doesn't compete with main model
  },
  "vector_memory": {
    "enabled": true,                           // semantic search on by default
    "directory": "vecmem",                     // relative to working dir
    "max_results": 5,
    "auto_index": true
  },
  "inference": {
    "max_tokens": 2048,                        // per turn (was 8192, caused repetition)
    "temperature": 0.3,
    "anti_prompts": ["</toolcall>", "</output>", ...]
  },
  "context_management": {
    "strategy": "SummaryAndShift",
    "keep_last": 20,
    "summarize_prompt": "Summarize the key decisions...",
    "max_summary_length": 2000
  }
}
```

## System Prompt (SystemPrompt.md v5)

Tuned specifically for Qwen3-8B:
- Strict response format: `<thinking>` then `<toolcall>` OR `<output>` — no exceptions
- 8 critical rules including "NEVER output text outside tags"
- Tool selection guide table (which tool for which job)
- PowerShell file operation patterns with examples
- Error handling instructions (read → fix → rebuild)
- Multi-step example (4-turn workflow: read → replace → build → confirm)
- After tool result: MUST respond with `<output>` tags

## Orchestration

The `AgentOrchestrator.ExecuteMultiStep()` loop:
1. Send user request to LLM → get response
2. `ExtractCleanResponse`: extract first `<thinking>` + first `<toolcall>` or `<output>` block only
3. `TrimToFirstClosingTag`: cut at first closing tag
4. `ParseLLMDecision`: detect tool call, direct answer, or invalid
5. If tool call → check policy → execute → add result to history → loop
6. If direct answer → return to user
7. If invalid → format retry (up to 2 times, remove bad response from history, inject as user msg)
8. Max 5 turns per task

**Key behaviors:**
- After tool result, appends directive: "You MUST respond with <thinking>...</thinking><output>...</output>"
- Multi-step tasks: allows up to 3 tool calls before pushing for final answer
- Format retry: removes bad tagless response from history, injects format error as user message
- Auto-save transcript on every tool call
- Manual anti-prompt enforcement: streaming loop checks for `</toolcall>`/`</output>` after each token

## Project Tree

```
ECAssistant/
├── ECAssistant.csproj               ← .NET 8 project (net8.0-windows)
├── SystemPrompt.md                  ← v5: Response format + tool selection guide + examples
├── appsettings.json                  ← Runtime config (bundled in build, copied to ~/ECAssistant/)
├── SUMMARY.md                        ← This file
├── ARCHITECTURE.md                   ← Architecture documentation
├── GAP_ANALYSIS.md                   ← Full gap analysis vs OpenClaw (40+ items)
├── GAP_FILTERED.md                   ← Filtered gap analysis (all items completed)
│
├── Program.cs                        ← Entry point: CLI loop, startup, tool registration
├── Orchestrator.cs                   ← Multi-step loop, tool policy, format retry, auto-fix
├── EColor.cs                         ← ANSI color helpers
│
├── Engine/
│   ├── EAgentEngine.cs               ← Core LLM engine: GGUF model, prompt building, inference
│   │                                     ExtractCleanResponse, TfidfEmbed, VectorMemory init
│   ├── ContextWindow.cs               ← Sliding window, auto-summarize at 50%, RemoveLastAssistant
│   ├── ConversationTranscript.cs      ← JSON transcript persistence
│   ├── TokenCounter.cs                ← LLamaSharp tokenizer-based counting
│   ├── EDecisionLoop.cs               ← v2: Interactive decision loop with real user input
│   ├── SecondaryModelLoader.cs        ← Optional second small model for summarization
│   └── SummaryService.cs              ← LLM-based context summarization
│
├── Tools/
│   ├── EToolBase.cs                   ← Abstract base: Name, Description, Rules, Examples
│   ├── ToolPolicy.cs                  ← 3-level permission: Allowed / ApprovalRequired / Blocked
│   ├── EPowerShell/
│   │   └── EPowerShellAgent.cs         ← PRIMARY TOOL: all file/system ops, 60s timeout
│   ├── EResearch/
│   │   └── EFileResearchTool.cs        ← Project-wide file scan
│   ├── EBackground/
│   │   └── EBackgroundExecTool.cs      ← Background process management (LLM-callable)
│   ├── EWeb/
│   │   └── EWebSearchTool.cs           ← DuckDuckGo web search (no auth)
│   ├── EDotnet/
│   │   └── EDotnetBuildTool.cs         ← Build + parse errors into structured output
│   ├── EGit/
│   │   └── EGitTool.cs                 ← Git operations with structured output
│   └── EExample/
│       └── EFileAnalyzer.cs           ← Example/template for new tools
│
├── Session/
│   └── AgentSession.cs                ← AgentSession + SessionManager (main, isolated, named)
│
├── Services/
│   ├── BackgroundProcessManager.cs    ← Non-blocking process execution + tracking
│   ├── FileWatcherService.cs          ← Workspace file change monitoring
│   └── Logger.cs                      ← Structured logging (file + console, 4 levels, no deps)
│
├── Memory/
│   ├── EMemoryManager.cs              ← Keyword memory with relevance scoring
│   └── VectorMemoryStore.cs           ← Semantic memory (TF-IDF, cosine similarity, JSON storage)
│
├── Config/
│   ├── EAgentConfig.cs                ← Full config model (nested JSON)
│   └── ContextParams.cs               ← Context parameter helpers
│
├── Analysis/
│   └── EContextAnalyzer.cs            ← v2: Project analysis (lines, TODOs, deps, orphans, cycles)
│
└── UI/
    ├── EGuiBase.cs                    ← Abstract UI interface
    └── EGuiConsole.cs                 ← Console implementation
```

## Git Workflow

**Repo:** `https://github.com/LLamaDudeX/ECAssistant.git` (branch: `main`)

```bash
cd <project-root>
git add -A
git commit -m "<descriptive message>"
git push
```

Always commit and push after changes — the maintainer tests on Windows.

**Status:** v9.22 — All gap items completed. 6 tools registered. Ready for Windows testing.