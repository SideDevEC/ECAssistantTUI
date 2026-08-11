# ECAssistant — Project Summary (v8 — 2026-08-12)

**Summary:** A local, offline AI agent built in C# .NET 8 using LLamaSharp. Embeds a GGUF model into its own runtime. Uses structured XML-style tags for tool calling and multi-step autonomous loops with persistent memory, sliding context windows, real tokenizer-based token counting, tool policy enforcement, approval gates, and multi-session management.

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0-windows`, Nullable enabled)
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk (Qwen3-8B-Q4_K_M.gguf)
- **Runtime:** Self-hosted, offline inference — no external API calls
- **Model:** Qwen3-8B-Q4_K_M, context: 32768 tokens, temp: 0.3
- **System Prompt:** `SystemPrompt.md` (v2.1 — loaded via `File.ReadAllText()` at startup)
- **Package deps:** `LLamaSharp`, LLamaSharp.Backend.Vulkan/Cuda12/CPU/Console, Microsoft.Extensions.Logging.Abstractions

## Project Tree
```
ECAssistant/
├── ECAssistant.csproj           ← .NET 8 project (net8.0-windows, Nullable)
├── SystemPrompt.md               ← v2.1: Identity + response schema + operating rules + stricter rules + memory guidelines (PRIMARY — loaded via File.ReadAllText)
├── SystemPrompt.json             ← Legacy system prompt (.json format, not used — EEngineSystemPrompt.cs exists as dead code)
├── appsettings.json              ← Runtime config (model, tools, memory, workspace settings)
├── SUMMARY.md                    ← This file
│
├── Program.cs                    ← Entry point: CLI loop, multi-step orchestration via AgentOrchestrator, help, memory commands
├── Orchestrator.cs               ← v2.2: Clean block detection — detects <toolcall>/<output> in clean response, acts accordingly; TrimToFirstClosingTag for drift trimming; fail-fast on streak
├── EColor.cs                     ← ANSI-colored console output helpers (Tag, TagBold, Write, WriteLineColored, etc.)
│
├── Engine/
│   ├── EAgentEngine.cs           ← Core LLM engine: loads GGUF model, builds prompts, runs inference via InteractiveExecutor
│   │   SystemPrompt.md loaded at startup via File.ReadAllText() → cached in _systemPromptText
│   │   BuildSystemToolsPrompt(): system prompt + tool definitions merged into one block
│   │   BuildFullPrompt(): system+tools → PERSISTENT MEMORY injection → windowed history → user request → stop directive
│   │   ExtractCleanResponse(): strips hallucination noise, extracts all well-formed <thinking>/<toolcall>/<output> blocks
│   │   GenerateAsync(): runs inference, applies ExtractCleanResponse, stores in transcript + contextWindow (unified)
│   ├── EEngineSystemPrompt.cs    ← DEAD CODE: still reads SystemPrompt.json (legacy). Not used by current code path.
│   ├── ContextWindow.cs          ← Sliding window with real token counting via TokenCounter + auto-summarize wiring to SummaryService
│   ├── TokenCounter.cs           ← Real LLamaSharp .Tokenize().ToList().Count + char-based fallback (~4 chars/token)
│   ├── ConversationTranscript.cs  ← Full disk-persisted conversation transcript (JSON), typed roles: system/user/assistant/tool_output
│   └── EDecisionLoop.cs          ← Interactive human-in-the-loop decision making (WIP placeholder — defaults "B" option)
│
├── Tools/
│   ├── EToolBase.cs              ← Abstract base: Name, Description, UsageExample, ExecuteAsync(); ToSystemPromptBlock() for unified injection; GetToolRules(), GetToolExample() for per-tool constraints
│   ├── EPowerShell/EPowerShellAgent.cs  ← Runs pwsh commands via cmd.exe wrapper. Rule: <command> = ENTIRE PS statement only.
│   ├── EResearch/EFileResearchTool.cs   ← Scans project directory, reads file content for LLM analysis (supports extension filtering)
│   └── EExample/EFileAnalyzer.cs        ← Example tool: reads/analyzes individual text files (line count, word count, preview)
│
├── Memory/EMemoryManager.cs       ← Persistent disk-based memory: save/query/archived across sessions. Keyword matching by category. Count property + Entries enumerable for engine integration.
│
├── Config/EAgentConfig.cs        ← Nested config classes (LlmConfig, InferenceConfig, ContextManagementConfig, SamplingConfig, MemoryConfig, etc.) loaded from appsettings.json
├── Config/ContextParams.cs       ← (Empty or minimal — all runtime settings in EAgentConfig)
│
├── Services/SummaryService.cs    ← LLM-based summarization of old context blocks. Falls back to extractive summary (first 3 messages). Injected into ContextWindow auto-summarize flow.
│
├── UI/EGuiBase.cs                ← Abstract UI interface: all user interactions flow through Program.Gui.* methods (WriteLine, PromptColored, PromptRaw, etc.)
├── UI/EGuiConsole.cs             ← Concrete console implementation wrapping EColor ANSI helpers
│
├── Analysis/EContextAnalyzer.cs  ← Static analysis helpers (WIP): scans project files, maps relationships, detects architecture type, identifies circular dependencies
│
└── obj/bin/obj/                  ← Build artifacts
```

## How It Works

**Startup flow:**
1. `Program.Main()` — config loaded from `appsettings.json` in user's home directory (`~/ECAssistant/appsettings.json`)
2. `EAgentEngine` constructor: loads GGUF model, initializes LLamaContext + InteractiveExecutor, **loads SystemPrompt.md via `File.ReadAllText()`**, initializes ContextWindow + Transcript, loads EMemoryManager from disk
3. Tools registered: EPowerShellAgent, EFileResearchTool (configurable extensions)
4. Orchestrator created with max turns / failure threshold from config
5. Enters CLI loop

**CLI loop (`Program.cs`):**
- `quit/exit` — saves transcript and exits
- `help` — shows command list
- `tools` — lists registered tools
- `clear-history` — clears context window + transcript
- `save-context` — saves transcript to `transcript.json` on disk
- `file-pick` — reads file content as LLM prompt
- `memory-save/query/stats` — persistent memory CRUD
- `clear-context` — clears context window only
- `analyze-project` — runs EContextAnalyzer cross-file analysis
- `interactive-decision` — enters EDecisionLoop (WIP)
- **default**: multi-step agent execution via `AgentOrchestrator.ExecuteMultiStep(input)`

**Engine generation flow (`EAgentEngine.GenerateAsync`):**
1. Add user message to transcript + context window
2. Build full prompt: system prompt (from SystemPrompt.md) → tool definitions → persistent memory injection → windowed history → user request → stop directive
3. Run inference via `_executor.InferAsync()`
4. Extract clean response via `ExtractCleanResponse()` — strips all hallucination noise, keeps well-formed XML-style blocks
5. Store in transcript + context window (unified — no duplicate string list)

**Prompt assembly (`BuildFullPrompt`):**
```
[system prompt from SystemPrompt.md]
[tool definitions: EPowerShellAgent, EFileResearchTool via ToSystemPromptBlock]
[PERSISTENT MEMORY — if relevant entries found in EMemoryManager]
[PREVIOUS TURNS — windowed history from ContextWindow with auto-summarize on budget overflow]
[user request wrapped in <user>...</user>]
[CRITICAL stop directive — use one tool then STOP, or produce final answer with <output>]
```

**Orchestrator flow (`AgentOrchestrator.ExecuteMultiStep`):**
1. Generate LLM response (with full context including system prompt, tools, memory, history)
2. TrimToFirstClosingTag — cuts at first `</toolcall>` or `</output>` boundary, preserves closing tag, drops trailing drift
3. ParseLLMDecision — detects block type: `<toolcall>`, `<output>`, or neither (error)
4. If `<toolcall>` → extract tool name + args → execute tool → add result to history → continue loop
5. If `<output>` → extract answer → return to user → stop loop
6. If neither → error message with response dump → stop loop
7. Auto-stop on failure streak (3 consecutive failures)

## v2 Response Schema (from SystemPrompt.md)

The LLM MUST respond using EXACT single-line format — no line breaks inside tags or between blocks:

**Tool call:** `<thinking>reasoning</thinking><toolcall>ToolName<arg>v</arg></toolcall>`
**Direct answer:** `<thinking>reasoning</thinking><output>answer</output>`

SystemPrompt.md contains: identity, response schema table (what NOT to do), conversation format (`<user>`, `<tooloutput>`), stricter rules (never invent tool results, stop after tool call), operating rules (read before modify, relative paths, workspace persistence, memory save/query guidelines).

## Architecture Diagram

```
User input
   ↓
Program.cs CLI loop → AgentOrchestrator.ExecuteMultiStep(input)
   ↓
EAgentEngine.GenerateAsync(prompt)
   ├─ Add to transcript (disk-persists for session resumption)
   └─ Add to ContextWindow (sliding window with real token counting + auto-summarize)
       ↓
  BuildFullPrompt()
       ├─ System prompt from SystemPrompt.md (loaded at startup via File.ReadAllText)
       ├─ Tool definitions (EPowerShellAgent, EFileResearchTool via ToSystemPromptBlock())
       ├─ Memory injection from EMemoryManager (queries persistent memory by keyword)
       └─ Windowed history (auto-summarize oldest 40% if over budget threshold)
               ↓
        _executor.InferAsync(fullPrompt) — LLamaSharp InteractiveExecutor
                     ↓
          ExtractCleanResponse() — strips noise, extracts all <thinking>/<toolcall>/<output> blocks
                     ↓
         Store in transcript + context window (unified — no separate string list for history)
```

## Components Deep Dive

### Engine (`EAgentEngine.cs`)
- **System prompt:** Loaded from `SystemPrompt.md` via `File.ReadAllText()` at startup (v7 fix — was incorrectly loading `.json` before, now corrected)
- **Memory injection:** Every turn queries `EMemoryManager` for relevant keywords from user request, injects top-5 results as "PERSISTENT MEMORY" section before history
- **ContextWindow:** Sliding window with real LLamaSharp tokenizer counting + auto-summarize wiring (SummarizeOldest drops oldest 40% when over budget)
- **Transcript:** Full JSON disk persistence for session resumption — loads on startup, saves via `save-context` command or on exit
- **ExtractCleanResponse:** Extracts ALL well-formed XML-style blocks by finding open→close tag pairs in order. No hallucination noise passes through.

### Orchestrator (`Orchestrator.cs`)
- **TrimToFirstClosingTag:** Cuts at first `</toolcall>` or `</output>` boundary — preserves closing tag, drops all trailing drift
- **ParseLLMDecision:** Priority check for `<toolcall>` (takes precedence over `<output>`), then `<output>`, then Unknown
- **Tool execution:** Whitelist-based — only registered tools can execute; args parsed from `<argname>value</argname>` via regex
- **Fail-fast:** After 3 consecutive tool failures, stops loop and reports to user

### Tool System (`EToolBase.cs`)
- Abstract base class — all tools extend EToolBase
- `ToSystemPromptBlock()`: One unified call produces Name → Description → Rules (from GetToolRules) → Examples (from GetToolExample)
- Each block injected into system prompt before history
- Result type: `EToolResult.Success()` / `EToolResult.Failure()` with metadata

### Memory (`EMemoryManager.cs`)
- Loads `.json` memory files from disk at startup
- Keyword-based query (case-insensitive split terms matching against key or content)
- Save entries by category (bugs, solutions, decisions, etc.)
- Persistent: saves to disk on Dispose(); `memory-save` command in CLI
- Exposes `Count` property and `Entries` enumerable for engine integration

### ContextWindow (`ContextWindow.cs`)
- Sliding window — tracks typed messages with token counts from `TokenCounter.Count()`
- Auto-summarize threshold at 75% of budget; triggers SummarizeOldest when exceeded
- SummarizeOldest: drops oldest 40% of messages, compresses via SummaryService (LLM-based if available, extractive fallback otherwise)
- Budget enforcement: `GetTotalTokens()` uses real LLamaSharp tokenizer

### TokenCounter (`TokenCounter.cs`)
- Uses `_context.Tokenize().ToList().Count` — actual LLamaSharp tokenizer output
- Fallback: char-based estimation (~4 chars/token with adjustments for punctuation, whitespace, uppercase)
- Used by ContextWindow, ConversationTranscript, and all message factories

## Commands / WIP / Known Issues

### What Works
- ✅ **System prompt from SystemPrompt.md** — loaded via `File.ReadAllText()` at startup (v7 fix)
- ✅ **Real tokenizer counting** — LLamaSharp `.Tokenize().ToList().Count` with char-based fallback
- ✅ **Memory injection** — persistent memory queried by keyword and injected into every prompt
- ✅ **Sliding context window** — auto-summarize when over token budget (75% threshold, drops 40%)
- ✅ **Transcript persistence** — full JSON disk save for session resumption (`save-context` command)
- ✅ **Multi-step orchestration** — detects `<toolcall>` / `<output>`, executes tools, loops until answer or max turns
- ✅ **Fail-fast on streak** — 3 consecutive failures stops loop automatically
- ✅ **Unified context** — no duplicate string list; transcript + context window are single source of truth
- ✅ **Content truncation** — `TrimToFirstClosingTag()` removes LLM hallucination drift after first valid block
- ✅ **Abstract UI** — `EGuiBase` interface allows swapping console for GUI/web with one-line change
- ✅ **Extensible tool system** — add new tool by extending EToolBase and registering in Program.cs

### WIP / Incomplete
- ⚠️ **DecisionLoop** (`EDecisionLoop.cs`) — Placeholder. Defaults "B" option. No real user input integration (simulated `WaitForUserInput`). AnalyzeTask uses simple heuristic (task length > 100 chars).
- ⚠️ **SummaryService** — Injected with `null` delegate in constructor. Falls back to extractive summary (first 3 messages). Real LLM summarization not wired yet.
- ⚠️ **EContextAnalyzer** (`Analysis/`) — Basic `using` import parsing only. No Roslyn analysis. Detects project type from file names (Controller, Repository, etc.) not real dependency resolution.
- ⚠️ **SystemPrompt.json / EEngineSystemPrompt.cs** — Legacy dead code. Still reads `.json` format but NOT used by current code path (engine reads `.md`). Dead code adds confusion.
- ⚠️ **Memory query** — Simple keyword substring matching only. No semantic/embedding-based similarity search.
- ⚠️ **Evaluation/Benchmarks** — No test suite or benchmark for measuring agent quality/reliability.

### Known Issues
- `EEngineSystemPrompt.cs` reads old `.json` format but is dead code — the engine correctly loads from `SystemPrompt.md` now. This file should be removed or repurposed.
- `EDecisionLoop.ExecuteInteractiveLoop()` defaults user answer to "B" — no real interactivity yet.
- No GUI beyond console — terminal-only experience.

## Gaps Analysis — What's Still Missing (2026-08-11)

### P0: System Prompt Fixed ✅
`SystemPrompt.md` now correctly loaded at startup via `File.ReadAllText()`. The `.json` file and `EEngineSystemPrompt.cs` are dead code — should be removed to avoid confusion.

### P1: Real Auto-Summarization ⚠️ Partial
`SummaryService.SummarizeAsync()` exists but is called with `null` delegate. Falls back to extractive summary (first 3 messages). Needs wiring to real LLM engine via injection from `Program.cs`.

### P2: Decision Loop — No Real Interactivity ⚠️
`EDecisionLoop` is a placeholder. Task analysis uses heuristic (length > 100 chars). Always defaults to "B" option. Needs actual user input capture and LLM-driven decision branching.

### P3: Knowledge Base / Semantic Memory 🟡
Memory uses keyword substring matching. No embeddings or vector search — would need FAISS or similar integration for semantic recall of relevant past decisions.

### P4: Evaluation & Benchmarks 🟡
No automated test suite. No replay of task scenarios against LLM to measure response quality over time. Hard to know if the agent is improving.

### P5: Dynamic Tool Discovery 🟢
Tools must be manually registered in `Program.cs`. A directory-scanning tool loader (scan `Tools/` for `.dll` or `.cs` and auto-register) would make it plugin-friendly.

### P6: Web Search / External APIs 🟢
Agent is fully offline. No web search, API calls, or external knowledge fetching. Could integrate curl/browser automation for real-time info gathering.

---

**Status:** v7.1 — builds successfully (0 errors). System prompt loaded from `SystemPrompt.md`. Orchestration deterministic (3 outcomes: tool call, output, error with fail-fast on streaks). ContextWindow manages sliding window with real LLamaSharp tokenizer counting + auto-summarize. Memory injection into prompts is live. Tool system extensible via EToolBase. **v7.1: History rendering fixed — tool outputs now properly formatted as `<tooloutput>ToolName<result>content</result></tooloutput>` and assistant responses as `<assistant>...</assistant>`, both with matching closing tags.**

**Added:** 2026-08-11
