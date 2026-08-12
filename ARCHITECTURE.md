# ECAssistant Architecture (v9.22 — 2026-08-12)

**Summary:** A local, offline AI agent in C# .NET 8 using LLamaSharp. Runs GGUF models locally with no external API calls. Uses XML-style response tags (`<thinking>`, `<toolcall>`, `<output>`) for reliable tool parsing. PowerShell is the primary tool for all file/system operations. Multi-step autonomous loops with dual memory systems (keyword + vector/semantic), sliding context windows with LLM-based summarization, structured logging, background process management, file watching, and 6 registered tools that self-register their rules at runtime.

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0-windows`, Nullable enabled)
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk
- **Runtime:** Self-hosted, offline — no external API calls
- **Tools:** 6 registered — EPowerShellAgent, EFileResearchTool, EBackgroundExec, EWebSearch, EDotnetBuild, EGitTool
- **Config:** `~/ECAssistant/appsettings.json` (user-editable, bundled as fallback)
- **System Prompt:** `SystemPrompt.md` v5 (tool-agnostic, tools self-register at runtime)
- **Working Directory:** `~/ECAssistant/` (all disk writes go here, build dir is read-only)

## Architecture Overview

```
Program.cs (entry point, CLI loop, startup, tool registration)
    │
    ├── Startup Flow:
    │   1. Create ~/ECAssistant/ if missing
    │   2. Copy appsettings.json from build dir if missing (first run only)
    │   3. Load config from ~/ECAssistant/appsettings.json (ALWAYS from working dir)
    │   4. Resolve model path: ~/ECAssistant/ first, build dir as fallback
    │   5. Create EAgentEngine (model, context, GPU layers, working dir)
    │   6. WireSummaryService (LLM-based context compaction)
    │   7. InitializeVectorMemory (TF-IDF vector store in ~/ECAssistant/vecmem/)
    │   8. Register 6 tools (self-inject rules into system prompt)
    │   9. Create SessionManager, BackgroundProcessManager, FileWatcherService
    │   10. Start RunAgentLoop
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
        │   │   ├── BuildFullPrompt: system prompt + tools + memory + windowed history
        │   │   │   ├── SystemPrompt.md (v5 — tool-agnostic)
        │   │   │   ├── Tool self-registration (ToSystemPromptBlock for each tool)
        │   │   │   ├── Memory injection (vector search top-3 + keyword search top-5)
        │   │   │   ├── Context window (sliding, auto-summarize at 50%)
        │   │   │   ├── History budget (reserve system + memory + max_tokens + buffer)
        │   │   │   └── Context-aware directive (allows multi-step, pushes for output after 3 tool calls)
        │   │   │
        │   │   ├── Inference: LLamaSharp InteractiveExecutor
        │   │   │   ├── Token streaming to console (dim color, real-time)
        │   │   │   ├── Manual anti-prompt check (break on </toolcall> or </output>)
        │   │   │   └── 90s timeout
        │   │   │
        │   │   └── ExtractCleanResponse: first <thinking> + first <toolcall>/<output> only
        │   │
        │   ├── 2. TrimToFirstClosingTag: cut at first closing tag
        │   │
        │   ├── 3. ParseLLMDecision: detect toolcall, output, or invalid
        │   │
        │   ├── 4a. If tool call:
        │   │   ├── ToolPolicy.Check (Allowed / ApprovalRequired / Blocked)
        │   │   ├── ExecuteTool → run tool → get result
        │   │   ├── Append directive: "MUST respond with <output> tags"
        │   │   ├── Track completed step for task context
        │   │   ├── AddToolResult (with auto-save transcript)
        │   │   └── Continue loop
        │   │
        │   ├── 4b. If direct answer (<output>):
        │   │   └── Return OrchestratorResult (goal achieved)
        │   │
        │   └── 4c. If invalid (no tags):
        │   ├── Remove bad response from history (both context window + transcript)
        │   ├── InjectFormatRetry as user message (not tool result)
        │   ├── Retry up to 2 times
        │   └── After 2 failures → stop with error
        │
        └── Return: OrchestratorResult (FinalOutput, ToolCallsMade, Status)
```

## Tool System

### Tool Registration (Runtime Self-Registration)

```
EToolBase (abstract)
├── Name, Description, UsageExample
├── GetToolRules() → tool-specific rules string
├── GetToolExample() → example tool calls
├── ToSystemPromptBlock() → assembles all into system prompt
└── ExecuteAsync(args) → runs the tool

Registered at startup:
1. EPowerShellAgent (workingDir)     — file ops, shell commands, 60s timeout
2. EBackgroundExecTool (bgMgr, dir)   — background process management
3. EWebSearchTool ()                 — DuckDuckGo web search
4. EDotnetBuildTool (dir)            — build + parse errors
5. EGitTool (dir)                    — git operations with structured output
6. EFileResearchTool (dir, exts)     — project-wide file scan
```

### Tool Policy

3 permission levels checked before every tool execution:
- **Allowed** — runs immediately
- **ApprovalRequired** — console `[y/N]` prompt
- **Blocked** — rejected with message

## Memory System (Dual)

### 1. Keyword Memory (EMemoryManager)
- JSON files on disk (`~/ECAssistant/Memory/`)
- Relevance scoring: key match (3pts), word match (2pts), content match (1pt)
- Confidence weighting affects ranking
- Category filter with boost/penalty
- Injected into every prompt (top 5 results)

### 2. Vector Memory (VectorMemoryStore)
- TF-IDF embeddings (256-dim hash-based, L2 normalized)
- Cosine similarity for search ranking
- JSON storage (`~/ECAssistant/vecmem/vectors.json`)
- No external dependencies (no FAISS, no Python)
- Injected into every prompt (top 3 results)
- Config: `vector_memory.enabled/directory/max_results/auto_index`

### Memory Injection Flow

```
GetMemoryInjection(query):
  1. Vector search → top 3 semantic results
  2. Keyword search → top 5 keyword results
  3. Combine and inject into prompt as "RELEVANT MEMORIES" section
```

## Context Management

### ContextWindow (Sliding Window)
- Real token counting via LLamaSharp tokenizer
- Auto-summarize at 50% of max tokens (was 75%, too late)
- `OverflowStrategy = TruncateAndReprefill` (was ThrowException, crashed)
- Hard history budget: reserves space for system prompt + memory + max_tokens + buffer
- `RemoveLastAssistantMessage()` for format retries (removes bad response from history)

### SummaryService
- LLM-based context compaction (uses the engine's own model)
- Wired via `WireSummaryService()` at startup
- Optional: can use SecondaryModelLoader for compaction instead

### Conversation Transcript
- Auto-saved on every tool call (crash recovery)
- JSON format: messages with role + content + timestamp
- Loaded on startup for session resumption
- Shows last 3 user messages from previous session

## Inference Pipeline

```
BuildFullPrompt:
  1. SystemPrompt.md (v5 — tool-agnostic, loaded from working dir)
  2. Tool self-registration (6 tools inject their rules)
  3. Memory injection (vector + keyword search results)
  4. Windowed history (sliding context window, budget-trimmed)
  5. Context-aware directive:
     - No tool results: "Use ONE tool call per response"
     - 1-2 tool results: "If you have the answer, use <output>. If you need more, call another tool."
     - 3+ tool results: "If you have enough information, give your final answer with <output>."

Inference:
  1. LLamaSharp InteractiveExecutor.InferAsync (streaming)
  2. Token streaming to console (dim color, real-time)
  3. Manual anti-prompt check after each token (break on </toolcall> or </output>)
  4. 90s timeout
  5. Max 2048 tokens per turn

Post-inference:
  1. ExtractCleanResponse: first <thinking> + first <toolcall>/<output> only
  2. Return to orchestrator
```

## Response Parsing

```
ParseLLMDecision(response):
  1. Find <toolcall>...</toolcall> → WantsToolCall=True, parse tool name + args
  2. Find <output>...</output> → WantsDirectAnswer=True, extract answer
  3. Neither found → format retry (remove bad response, inject as user message, retry 2x)
```

## Design Decisions

### 1. PowerShell as Primary Tool
All file operations via `EPowerShellAgent`. No separate C# file op classes. Every LLM knows PowerShell natively.

### 2. Tool Self-Registration
SystemPrompt.md is tool-agnostic. Tools inject their own rules at runtime via `ToSystemPromptBlock()`. Adding tools = zero SystemPrompt.md edits.

### 3. Dual Memory (Keyword + Vector)
Keyword memory for exact matches. Vector memory for semantic similarity. Both injected into every prompt. No external dependencies for vector search.

### 4. Manual Anti-Prompt Enforcement
LLamaSharp's built-in anti-prompt matching doesn't reliably catch tokenized closing tags. Manual check after each token: if accumulated output contains `</toolcall>` or `</output>`, break immediately.

### 5. ExtractCleanResponse (First Block Only)
Model sometimes generates multiple `<thinking>+<toolcall>` blocks in one response. Only the first complete block is kept. Prevents repetition loops.

### 6. Format Retry with History Cleanup
When model produces tagless text: remove the bad response from history (so model doesn't learn from its own mistake), inject format error as user message (stronger signal than tool result), retry up to 2 times.

### 7. Post-Tool Directive
After every tool result, appends: "You MUST respond with <thinking>...</thinking><output>...</output>". The 8B model needs explicit instruction to use tags after receiving tool output.

### 8. Working Directory Isolation
All disk writes go to `~/ECAssistant/`. Build directory is read-only (config/model fallback only). App auto-creates working dir and copies default config on first run.

### 9. Safe LLM Defaults
- `context_size: 8192` (was 32768, caused OOM)
- `gpu_layers: 15` (was 35, caused memory corruption)
- `max_tokens: 2048` (was 8192, caused repetition)
- `OverflowStrategy: TruncateAndReprefill` (was ThrowException, crashed on overflow)
- Auto-summarize at 50% (was 75%, too late)

### 10. Config from Working Directory
Config is ALWAYS loaded from `~/ECAssistant/appsettings.json`. Never from build dir. User edits are always respected.

## Feature Status

| Feature | Status | Notes |
|---------|--------|-------|
| Local GGUF inference | ✅ Live | LLamaSharp 0.27.0 |
| PowerShell tool (primary) | ✅ Live | 60s timeout, all file/system ops |
| EFileResearchTool | ✅ Live | Project-wide scan |
| EBackgroundExec | ✅ Live | LLM can start background tasks |
| EWebSearch | ✅ Live | DuckDuckGo, no auth |
| EDotnetBuild | ✅ Live | Structured error parsing |
| EGitTool | ✅ Live | Structured git output |
| XML response parsing | ✅ Live | Strict tag enforcement |
| Tool self-registration | ✅ Live | Runtime via ToSystemPromptBlock() |
| Multi-step orchestration | ✅ Live | 5 turns max, 3 before pushing for answer |
| Tool policy + approval gates | ✅ Live | 3 levels |
| Format retry | ✅ Live | 2 retries, history cleanup, user message injection |
| Post-tool output directive | ✅ Live | Forces <output> tag usage after tool results |
| Session management | ✅ Live | Main, isolated, named |
| Real tokenizer counting | ✅ Live | LLamaSharp .Tokenize() |
| Keyword memory (relevance scored) | ✅ Live | 3-tier scoring + confidence weighting |
| Vector memory (TF-IDF semantic) | ✅ Live | 256-dim, cosine similarity, no deps |
| Sliding context window | ✅ Live | Auto-summarize at 50%, TruncateAndReprefill |
| LLM-based summarization | ✅ Live | SummaryService wired to engine |
| Transcript auto-save | ✅ Live | On every tool call |
| Background process manager | ✅ Live | Start/track/kill, CLI commands |
| File watcher | ✅ Live | Workspace monitoring, skips bin/obj/.git |
| Structured logging | ✅ Live | File+console, 4 levels, no deps |
| DecisionLoop v2 | ✅ Live | Real user input, LLM-driven |
| EContextAnalyzer v2 | ✅ Live | Lines, TODOs, deps, orphans, cycles |
| Secondary model support | ✅ Live | Optional, for summarization |
| Config hot-reload | ✅ Live | reload-config command |
| Model hot-swap | ✅ Live | swap-model command |
| Clipboard support | ✅ Live | Windows clipboard read/write |
| Working directory isolation | ✅ Live | All writes to ~/ECAssistant/ |
| Auto-create working dir | ✅ Live | First run copies default config |
| Token streaming | ✅ Live | Dim color, real-time console output |
| Error auto-fix loop | ✅ Live | Injects [AUTO-FIX] prompt after build errors |
| PowerShell timeout | ✅ Live | 60s default |

**Status:** v9.22 — All features operational. Ready for Windows testing.