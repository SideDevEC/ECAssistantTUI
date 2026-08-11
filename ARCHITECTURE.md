# ECAssistant Architecture Summary (v7 — 2026-08-11)

**Summary:** A local, offline AI agent built in C# .NET 8 using LLamaSharp. Runs a large language model locally via GGUF weights. Uses structured XML-style response tags (`<toolcall>`, `<output>`, `<thinking>`) for reliable tool parsing and multi-step autonomous loops with persistent memory, sliding context windows, and real tokenizer-based token counting. **v7: System prompt correctly loaded from `SystemPrompt.md` via `File.ReadAllText()`. Legacy `.json` path identified as dead code.**

## Key Facts
- **Language:** C# .NET 8 console application (`net8.0-windows`, Nullable enabled)
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk (Qwen3-8B-Q4_K_M.gguf)
- **Runtime:** Self-hosted, offline inference — no external API calls
- **Config:** `SystemPrompt.md` (v2.1: identity + response schema + operating rules), `appsettings.json` (runtime settings), `EEngineSystemPrompt.cs` (legacy dead code — reads old `.json`, not used)
- **Package deps:** `LLamaSharp`, LLamaSharp.Backend.Vulkan/Cuda12/CPU/Console, Microsoft.Extensions.Logging.Abstractions

## Architecture Overview

```
Program.cs (entry point, CLI loop)
    │
    ├── AgentOrchestrator.ExecuteMultiStep(goal)
    │       │
    │       ├── EAgentEngine.GenerateAsync(prompt)
    │       │       │
    │       │       ├── BuildFullPrompt(): system + tools → memory injection → windowed history → user request → stop directive
    │       │       │
    │       │       └── _executor.InferAsync(fullPrompt) — LLamaSharp InteractiveExecutor
    │       │               │
    │       │               └── ExtractCleanResponse() — strips noise, extracts all structured blocks
    │       │                       │
    │       │                       └── Store in transcript + context window (unified — no duplicate string list)
    │       │
    │       ├── TrimToFirstClosingTag() — cuts drift after first </toolcall> or </output>
    │       ├── ParseLLMDecision() — detects <toolcall>, <output>, or error
    │       └── ExecuteTool(toolName, args) — whitelist-based tool execution with fail-fast on streak
    │
    ├── CLI Commands: quit/exit, help, tools, clear-history, save-context,
    │                  file-pick, memory-save/query/stats, clear-context,
    │                  analyze-project, interactive-decision
    │
    └── Tool System (v3 — Unified Block Injection)
            ├── EToolBase (abstract base: Name, Description, UsageExample, ExecuteAsync, ToSystemPromptBlock)
            ├── EPowerShellAgent (powershell command execution via cmd.exe wrapper)
            ├── EFileResearchTool (project-wide file scan + content read for LLM analysis)
            └── EFileAnalyzer (example tool: reads individual text files, reports stats)
```

## Component Details

### 1. Engine (`EAgentEngine.cs`) — The Core LLM Executor

**System Prompt Loading (v7 fix):**
- At startup, `SystemPrompt.md` is loaded via `File.ReadAllText()` from the project directory
- Stored in `_systemPromptText` field — cached for all LLM calls
- Legacy path `SystemPrompt.json` + `EEngineSystemPrompt.Load()` is dead code (not invoked by current flow)

**Prompt Assembly (`BuildFullPrompt`):**
```
┌─────────────────────────────────────────────┐
│ System prompt from SystemPrompt.md          │  ← loaded once at startup
│ Tool definitions (EPowerShellAgent, etc.)   │  ← ToSystemPromptBlock() per tool
│ PERSISTENT MEMORY                           │  ← queried from EMemoryManager by keyword
│ PREVIOUS TURNS                              │  ← windowed history from ContextWindow
│ <user>current task</user>                   │  ← user request
│ CRITICAL stop directive                     │  ← "use one tool then STOP" or "produce final answer with <output>"
└─────────────────────────────────────────────┘
```

**Generation (`GenerateAsync`):**
1. Add user message to transcript + context window (unified — no separate string list)
2. Build full prompt
3. Run inference via `_executor.InferAsync()` (90s timeout with CancellationToken)
4. Extract clean response: `ExtractCleanResponse()` strips all noise, keeps well-formed XML-style blocks
5. Store in both transcript and context window simultaneously
6. Return clean response to orchestrator

**Context Management:**
- `ContextWindow` — sliding window with real token counting via `TokenCounter.Count()` (LLamaSharp tokenizer)
- `ConversationTranscript` — full JSON disk persistence for session resumption (auto-load on startup, save via `save-context` or on exit)
- `EMemoryManager` — persistent keyword-based memory across sessions, loaded eagerly at engine startup

### 2. Orchestrator (`Orchestrator.cs`) — The Decision Brain

**Three outcomes per LLM response:**
1. `<toolcall>` detected → execute tool → add result to history → continue loop
2. `<output>` detected → extract answer → return to user → stop loop
3. Neither → error message with response dump → stop loop

**Key mechanisms:**
- `TrimToFirstClosingTag()`: Cuts at first `</toolcall>` or `</output>` boundary, preserves closing tag text, drops all trailing drift/hallucination
- `ParseLLMDecision()`: Priority check — `<toolcall>` takes precedence over `<output>`, both verified with open+close tag pairs
- **Fail-fast**: After 3 consecutive tool failures (`IsFailureStreak()`), loop stops and reports to user
- Max turn limit from config (`_maxTurns`)

**Tool execution:**
- Whitelist-based: only registered tools can execute (validated via `toolWhitelist` set)
- Args parsed from `<argname>value</argname>` tags using regex
- Tool results stored with metadata (exit code, output chars)

### 3. Tool System (`Tools/`) — Extensible Plugin Framework

**Base class (`EToolBase.cs`):**
```csharp
public abstract class EToolBase {
    public abstract string Name { get; }              // Unique identifier
    public abstract string Description { get; }       // Human-readable summary
    public abstract string UsageExample { get; }      // Example call for LLM
    public abstract Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments);
    
    public virtual string GetExtendedSystemPrompt() => "";  // Additional system prompt text
    public virtual string GetToolRules() => "";           // Strict policy constraints
    public virtual string GetToolExample() => "";         // XML-style example for LLM
    public string ToSystemPromptBlock() { ... }           // Unified: Name → Desc → Rules → Examples
}
```

**Registered tools:**
- **EPowerShellAgent** — Full filesystem + PowerShell commands. Executes via `cmd.exe /c powershell` wrapper (avoids stream conflicts). Single `<command>` tag = ENTIRE PS statement. Rule-enforced: no other tags allowed.
- **EFileResearchTool** — Scans project files by extension, reads content for LLM analysis. Extension filtering configurable from `appsettings.json`.
- **EFileAnalyzer** (Example) — Reads individual text files, reports line/word count + 500-char preview. Demonstrates the tool pattern.

### 4. Context Management

**ContextWindow (`Engine/ContextWindow.cs`):**
- Sliding window with configurable max token budget from `appsettings.json`
- Auto-summarize threshold at 75% of budget
- When over budget: drops oldest 40% of messages, compresses remaining via `SummaryService.SummarizeAsync()` (LLM-based if wired, extractive fallback otherwise)
- Real token counting via `TokenCounter.Count()` for every message

**TokenCounter (`Engine/TokenCounter.cs`):**
- Uses `_context.Tokenize().ToList().Count` — actual LLamaSharp tokenizer output from loaded GGUF model
- Fallback: char-based estimation (~4 chars/token baseline, adjusted for punctuation/whitespace/uppercase patterns)
- Used by ContextWindow, ConversationTranscript, and all message type factories

**ConversationTranscript (`Engine/ConversationTranscript.cs`):**
- Full JSON disk persistence — saves entire conversation history as typed messages (system/user/assistant/tool_output)
- Auto-loads on engine startup for session resumption
- Save triggered by `save-context` CLI command, or at program exit
- Includes timestamp + estimated tokens per message

### 5. Memory System (`Memory/EMemoryManager.cs`)

**Storage:** `.json` files in a memory directory (default: `~/ECAssistant/Memory/`)
**Query:** Keyword substring matching — splits search term by spaces, matches any term against entry key or content (case-insensitive)
**Categories:** Entries tagged by category (bugs, solutions, decisions, general) for filtered querying
**Lifecycle:** Load on startup (`Load()`), save on Dispose() / `save-context` command

**Engine integration:**
- Eagerly loaded at engine constructor (not lazy)
- Every generation turn: `GetMemoryInjection(userRequest)` queries relevant keywords, injects top-5 results into prompt as "PERSISTENT MEMORY" section
- Exposes `Count` property and `Entries` enumerable for diagnostics

### 6. Configuration (`Config/EAgentConfig.cs`)

All runtime settings in nested JSON structure (`appsettings.json`):
- **LlmConfig:** model path, context size (32768), GPU layers (35), threads (-1 auto)
- **InferenceConfig:** max tokens (8192), temperature (0.3), top_p (0.9), repeat penalty (1.1), anti-prompts list (`</s>`, `\n```\n`, `User:`, `---`, `</toolcall>`, `</output>`)
- **SamplingConfig:** temperature, top_p, top_k (40), repeat_penalty, mirostat settings
- **ContextManagementConfig:** strategy ("SummaryAndShift"), keep_last (20), shift_at_messages (40), summarize_prompt text, auto_shift_on_generate (true)
- **MemoryConfig:** data_path ("Memory"), enabled (true), max_entries (500)
- **ToolsConfig:** PowerShell config (enabled, output chars limit 50000), FileResearchTool config (extensions, max_chars_per_file 15000)
- **AgentConfig:** working_directory, allow_delete, allowed_extensions
- **WorkspaceConfig:** path ("Workspace"), allow_delete, max_size_mb (500)

### 7. UI Layer (`UI/`) — Abstract Interface

**EGuiBase** — Abstract base class for ALL user I/O:
- `WriteLine`, `WriteLineColored`, `WriteRaw`, `BlankLine` — output methods
- `PromptColored`, `PromptRaw` — input methods (return null on EOF)
- `InfoColored`, `WarningColored` — status/warning display
- `LogInternal` — debug logging (overridable, virtual default)
- `WriteRawDirect` — raw write for streaming inference (no newline)

**EGuiConsole** — Concrete implementation wrapping EColor ANSI helpers. One-line swap to change UI paradigm (console → GUI → web).

### 8. Analysis (`Analysis/EContextAnalyzer.cs`) — Project Intelligence

Scans project files, maps relationships via `using` import parsing, builds architecture map. Detects:
- Project type (ASP.NET MVC, REST API, Console Application)
- Circular dependencies
- Potentially unused files
- Dependency graph

**Limitation:** Simple string-based `using` parsing only — not real Roslyn analysis. No semantic understanding of code structure beyond import relationships.

### 9. Decision Loop (`Engine/EDecisionLoop.cs`) — WIP Interactive Mode

Interactive human-in-the-loop decision making:
- Analyzes task complexity (heuristic: length > 100 chars)
- Presents options with Pro/Con trade-offs
- Waits for user input
- Executes chosen option

**Current state:** Placeholder. Defaults to "B" option. `WaitForUserInput()` is simulated — no real console capture in non-interactive mode. Simple heuristic for task analysis (not LLM-driven).

### 10. Summary Service (`Services/SummaryService.cs`)

Compresses old conversation messages:
- **LLM path:** Calls injected delegate with prompt asking for 2-3 sentence summary
- **Extractive fallback:** Takes first 3 messages' content when LLM not available
- Called automatically by `ContextWindow.SummarizeOldest()` when over token budget

## Legacy / Dead Code

**`SystemPrompt.json` + `EEngineSystemPrompt.cs`:** 
Legacy system prompt loading from JSON format. Currently NOT used — engine correctly loads from `SystemPrompt.md` via `File.ReadAllText()`. The `EEngineSystemPrompt.Load()` method reads `.json` but is never called in the current code path. This file should be removed or repurposed to avoid confusion.

## Architecture Diagram (Detailed)

```
┌──────────────────────────────────────────────────────────────┐
│                        Program.cs                             │
│  CLI Loop: quit/help/tools/clear-history/save-context/        │
│    file-pick/memory-save/query/stats/clear-context/           │
│    analyze-project/interactive-decision/<free-text>           │
└──────────────────────┬───────────────────────────────────────┘
                         │ ExecuteMultiStep(input)
                         ▼
┌──────────────────────────────────────────────────────────────┐
│                  AgentOrchestrator                             │
│  TrimToFirstClosingTag → ParseLLMDecision → ExecuteTool      │
│  Fail-fast on 3 consecutive failures                          │
└──────────────────────┬───────────────────────────────────────┘
                         │ GenerateAsync(prompt)
                         ▼
┌──────────────────────────────────────────────────────────────┐
│                     EAgentEngine                               │
│  BuildFullPrompt: system + tools → memory → windowed history   │
│  └── _executor.InferAsync(fullPrompt) — LLamaSharp            │
│      ExtractCleanResponse() — strip all noise                  │
└──────────────────────┬───────────────────────────────────────┘
                         │
          ┌──────────────┼──────────────┐
          ▼              ▼              ▼
    ContextWindow   Transcript     EMemoryManager
    (sliding        (JSON           (keyword-based
     window)         disk            persistent)
                    persistence)
```

## Prompt Flow (Every Generation Turn)

```
BuildFullPrompt(userRequest)
    │
    ├── [System prompt from SystemPrompt.md]
    │    "You are ECAssistant... you MUST respond using EXACT single-line format..."
    │    + operating rules, stricter rules, memory guidelines, conversation format
    │
    ├── [Tool Definitions]
    │    ### EPowerShellAgent (Name → Description → Rules → Examples via ToSystemPromptBlock)
    │    ### EFileResearchTool (Name → Description → Examples via ToSystemPromptBlock)
    │
    ├── [PERSISTENT MEMORY — if relevant entries found]
    │    "These are past decisions, patterns, and lessons that may help you:"
    │    [from EMemoryManager.Query(userRequest, maxResults: 5)]
    │
    ├── [PREVIOUS TURNS — windowed history from ContextWindow]
    │    <user>...</user>
    │    </tooloutput><tooloutput>...</tooloutput></tooloutput>
    │    [auto-summarize oldest 40% if over budget at 75% threshold]
    │
    ├── [<user>current request</user>]
    │
    └── [-- CRITICAL stop directive --]
         "Use ONE tool call then STOP" OR "Produce final answer with <output>"
```

## Current State / WIP

| Component | Status | Notes |
|-----------|--------|-------|
| SystemPrompt.md loading | ✅ Fixed (v7) | Loaded via `File.ReadAllText()` at startup — was `.json` before |
| SystemPrompt.json / EEngineSystemPrompt.cs | ⚠️ Dead code | Reads old `.json`, not used by current engine path |
| Real tokenizer counting | ✅ Live | LLamaSharp `.Tokenize().ToList().Count` with char fallback |
| Memory injection into prompts | ✅ Live | Keyword query from EMemoryManager, injected every turn |
| Sliding context window | ✅ Live | Auto-summarize at 75% budget threshold, drops 40% oldest |
| Transcript persistence | ✅ Live | JSON disk save/load for session resumption |
| Multi-step orchestration | ✅ Live | Detects toolcall/output/error, loops until answer or max turns |
| Fail-fast on streak | ✅ Live | 3 consecutive failures stops loop automatically |
| Unified context (no duplicates) | ✅ Live | Transcript + ContextWindow only — no legacy string list |
| Content truncation (drift trim) | ✅ Live | TrimToFirstClosingTag() after every LLM response |
| Abstract UI layer | ✅ Live | EGuiBase interface, EGuiConsole concrete implementation |
| Extensible tool system | ✅ Live | Add tool via EToolBase + RegisterTool() |
| DecisionLoop | ⚠️ WIP | Placeholder — defaults "B", no real user input |
| SummaryService (real LLM) | ⚠️ Partial | Injected with null delegate, falls back to extractive summary |
| Analysis (EContextAnalyzer) | ⚠️ WIP | Basic `using` parsing only, no Roslyn analysis |
| Evaluation / benchmarks | 🟡 Missing | No automated test suite or quality measurement |

**Status:** v7.1 — builds successfully (0 errors). System prompt loaded from `SystemPrompt.md`. Orchestration deterministic (3 outcomes: tool call, output, error). ContextWindow manages sliding window with real LLamaSharp tokenizer counting + auto-summarize. Memory injection into prompts is live. Tool system extensible via EToolBase.

**v7.1 Fix: History rendering — tool outputs and assistant responses now use proper XML-style tags with matching open/close blocks, so the LLM can parse them correctly in conversation history.**

**Added:** 2026-08-11
