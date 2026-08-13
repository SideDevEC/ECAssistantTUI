# ECAssistant — Project Summary (v10.17.0 — 2026-08-13)

**Summary:** A local, offline AI agent built in C# .NET 8 using LLamaSharp. Cross-platform (Windows + macOS). Loads GGUF models from disk — no API calls, no cloud, fully self-contained. Uses `<lm>` container tag for noise-proof response parsing with XML-style inner tags for tool calling. Multi-step autonomous loops, dual memory (keyword + vector/semantic), sliding context windows, self-correction with failure loop detection, project context awareness, task decomposition, surgical code editing, 7 registered tools. Secondary model (Phi-4-mini) with fully configurable sampling params and anti-prompts. v10.13: Parallel multi-tool execution. v10.15: KV cache hybrid rewind, off-by-one fixes, `<llm>`→`<lm>` rename, sub-task advancement fixes. v10.16: Cross-platform migration — EShellAgent, dual system prompts, Mac support. v10.17: StepMapper (two-phase planning: decompose → map → execute), automated test framework (17 tests, non-interactive UI harness).

## Key Facts
- **Language:** C# .NET 8 console app (`net8.0`, cross-platform, Nullable enabled)
- **Platforms:** Windows (PowerShell) + macOS (zsh) — OS detected at runtime
- **LLM Backend:** LLamaSharp 0.27.0 — loads GGUF models from disk
- **Backends:** Cuda12 (Windows/NVIDIA), Vulkan (all platforms/Metal), Cpu (always)
- **Runtime:** Self-hosted, offline inference — no external API calls
- **Primary Model:** Qwen_Qwen3-8B-Q4_K_M (bartowski, 4.7 GiB)
- **Secondary Model:** microsoft_Phi-4-mini-instruct-Q4_K_M (bartowski, 2.3 GiB)
- **Models location:** `~/Agent/models/` (gitignored, absolute paths in config)
- **Repo:** `github.com/LLamaDudeX/ECAssistant.git` (branch: `main`)
- **Latest Tags:** `mac-safe` (v10.16.0), `pre-mac` (v10.15.8)
- **Executor:** InteractiveExecutor with KV cache (static prefix prefilled once, incremental feed per turn)
- **Secondary Executor:** StatelessExecutor (fresh context per call, no cache)
- **Source files:** 34 .cs files, dual system prompts

## What's New (v10.15 — Session 2026-08-13)

### KV Cache Hybrid Rewind (v10.14-v10.15)
- Format retry: fast `LoadState` rewind as default (state saved before `InferAsync`)
- Full `ResetAndRebuildCacheAsync` as fallback if `LoadState` fails 2x consecutively
- After rebuild, re-feed conversation history from context window into fresh KV cache
- Fixes `llama_decode invalidInputBatch` errors from stale KV cache state

### Off-By-One Closing Tag Fixes (v10.14.2)
- `</output>` is 9 chars — code used `oc + 8`, truncating the final `>`
- `</toolcall>` is 11 chars — code used `tcEnd + 10`, same issue
- Caused `ParseLLMDecision` to fail → endless format retry loops
- Root cause of "WantsDirectAnswer=false, toolcalls=0" mystery

### `<llm>` → `<lm>` Tag Rename (v10.15.2)
- Model frequently hallucinated `<\lllm>` (extra l) for `</llm>`
- Double-l in `llm` triggers character repetition artifacts in token generation
- Renamed to `<lm>` — shorter, no repeated chars, still unambiguous
- 96 occurrences across 5 files

### Generation Cue Fix (v10.15.4)
- Cue was `<assistant><lm>` — model started AFTER `<lm>`, never output it
- Changed to `<assistant>` only — model must output `<lm>` itself
- Token stream now shows both opening and closing tags

### Sub-Task Advancement Fixes (v10.15.1-v10.15.8)
- Single tool success: advance ALL remaining sub-tasks (one command can cover multiple steps)
- Tool failure: log to `_toolCallLog`, feed error via `AddToolResult`, inject directive
- Batch success: advance all covered sub-tasks
- **Guard: `Count > 1` check** prevents infinite loop on single-step tasks (AdvanceSubTask is no-op when Count <= 1)

## What's New (v10.16 — Cross-Platform Migration)

### EShellAgent (renamed from EPowerShellAgent)
- `EPowerShellAgent` → `EShellAgent` — not PowerShell-specific anymore
- OS detection at runtime: `powershell.exe` (Windows) / `/bin/zsh` (Mac)
- Temp scripts: `.ps1` (Windows) / `.sh` (Mac)
- Tool examples, rules, description all OS-aware
- Namespace: `ECAssistant.Tools.Shell`

### Dual System Prompts
- `SystemPrompt.Windows.md` — PowerShell cmdlets (Get-Content, Set-Content, etc.)
- `SystemPrompt.Mac.md` — zsh equivalents (cat, echo >, cp, grep, sed)
- `SystemPrompt.md` — legacy fallback
- Engine loads correct file based on OS

### Cross-Platform .csproj
- `net8.0-windows` → `net8.0`
- GPU backends via MSBuild conditionals (Cuda12 Windows, Vulkan all, Cpu always)
- LLamaSharp 0.27.0 supports `net8.0` (verified via nuspec)
- Builds and runs on macOS (arm64)

## Parallel Multi-Tool Execution (v10.13)

### How It Works
1. Model emits multiple `<toolcall>` tags in one `<lm>` response
2. `ExtractCleanResponse` extracts ALL toolcall blocks
3. `ToolDependencyAnalyzer` inspects tool types + args for dependencies
4. Independent toolcalls run in parallel via `Task.WhenAll`
5. All results combined into one `<tooloutput>` block

### Dependency Heuristics
- Same tool, different files → independent (parallel)
- Write tool on file A + read tool on file A → sequential (read first)
- Build/git → always after code-modifying tools
- Different tools, different targets → independent

## Registered Tools (7)

| Tool | Purpose | Key Feature |
|------|---------|-------------|
| **EShellAgent** | File/system ops, any shell command | OS-aware (PowerShell/zsh), 60s timeout |
| **EFileResearchTool** | Project-wide file scan | Multi-file content scan |
| **EBackgroundExec** | Background process management | start/status/output/kill |
| **EWebSearch** | Web search | DuckDuckGo API, no auth |
| **EDotnetBuild** | .NET build/test/format | Structured errors, test-filter |
| **EGitTool** | Git operations | status/diff/commit/push/pull/log |
| **ECodeEditor** | Surgical code editing | patch/diff/search/replace/insert/delete |

## Response Format (v10.15 — `<lm>` Container)

```
Tool call:
<lm><thinking>Brief reasoning</thinking><toolcall>ToolName<argname>value</argname></toolcall></lm>

Direct answer:
<lm><thinking>Brief reasoning</thinking><output>Answer to user</output></lm>
```

Rules:
1. FIRST token is always `<lm>`. LAST token is always `</lm>`.
2. Inside: ONE `<thinking>`, then ONE `<toolcall>` OR ONE `<output>`. Then `</lm>`. Then STOP.
3. Never write text outside `<lm>...</lm>`.
4. Can batch multiple shell commands with `;` in one toolcall.
5. Multiple `<toolcall>` tags allowed in one `<lm>` for parallel execution.

## Orchestration Flow

```
ExecuteMultiStep(goal):
  Loop (max 5 turns):
    1. GenerateAsync: BuildIncrementalInput → stream tokens → stop at </lm> → ExtractCleanResponse
    2. ESC check → if stopped: clear context, rebuild cache, return
    3. ParseLLMDecision: toolcall | output | invalid
    4a. Tool call → policy check → execute → advance sub-tasks → AddToolResult → directive → loop
    4b. Direct answer → return to user
    4c. Invalid → RemoveLastAssistantResponseAsync (hybrid rewind) → format retry → max 2
```

## Project Tree

```
ECAssistant/
├── ECAssistant.csproj               ← .NET 8 cross-platform (net8.0)
├── SystemPrompt.md                  ← Legacy fallback system prompt
├── SystemPrompt.Windows.md          ← Windows/PowerShell examples
├── SystemPrompt.Mac.md              ← Mac/zsh examples
├── appsettings.json                  ← Runtime config (absolute model paths)
├── MIGRATION_PLAN.md                 ← Cross-platform migration checklist
├── SUMMARY.md / ARCHITECTURE.md
│
├── Program.cs                        ← Entry point: CLI, startup, tool registration
├── Orchestrator.cs                   ← Multi-step loop, sub-task advancement, format retry
├── EColor.cs                         ← ANSI color helpers
│
├── Engine/
│   ├── EAgentEngine.cs               ← Core LLM engine, <lm> extraction, KV cache, hybrid rewind, GeneratePlanAsync
│   ├── ContextWindow.cs               ← Sliding window, auto-summarize
│   ├── ConversationTranscript.cs      ← JSON transcript persistence
│   ├── TokenCounter.cs                ← LLamaSharp tokenizer counting
│   ├── SecondaryModelLoader.cs        ← Configurable secondary model
│   ├── StepMapper.cs                  ← v10.17: Maps sub-tasks to concrete tool calls (ExecutionPlan)
│   ├── SummaryService.cs              ← LLM-based context summarization
│   ├── SelfCorrectionManager.cs       ← Failure loop detection, snapshots, rollback
│   ├── ProjectContextManager.cs       ← Project scan, dependency graph
│   ├── TaskPlanner.cs                 ← Task decomposition, sub-task tracking
│   ├── ToolDependencyAnalyzer.cs      ← Parallel tool dependency analysis
│   └── ParallelToolExecutor.cs        ← Parallel execution + result combining
│
├── Tools/
│   ├── EToolBase.cs                   ← Abstract base
│   ├── ToolPolicy.cs                  ← 3-level permission system
│   ├── EShell/EShellAgent.cs          ← OS-aware shell (PowerShell/zsh), 60s timeout
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
├── Config/EAgentConfig.cs
├── Analysis/EContextAnalyzer.cs
└── UI/
    ├── EGuiBase.cs                     ← Abstract UI + Truncate() centralized
    └── EGuiConsole.cs                  ← Console implementation
│
├── Testing/                            ← v10.17: Automated test framework
│   ├── EGuiTestHarness.cs              ← Non-interactive UI (captures output, scripts input)
│   ├── TestRunner.cs                   ← Orchestrates test scenarios, sandboxed dirs, assertions
│   └── EcaTests.cs                     ← 17 predefined test scenarios (7 tiers)
```

## What's New (v10.17 — Two-Phase Planning + Test Framework)

### StepMapper — Two-Phase Planning Pipeline

**Problem:** TaskPlanner decomposed requests into text steps ("what to do") but the LLM had to figure out on its own which tools to use and how to batch them. This led to:
- LLM making one tool call per step instead of batching
- Sub-task advancement heuristics (string matching on `;` and `&&`) — fragile, shell-only
- No tool mapping — LLM interpreted vague text steps with no execution blueprint

**Solution:** Added an intermediate mapping phase between decomposition and execution:

```
1. Secondary model (Phi-4-mini) → decompose goal into text steps ("what to do")
2. Main LLM (Qwen3-8B, stateless) → map steps to concrete tool calls ("how to do it")
3. Execution plan injected into context as system message
4. LLM executes following the plan
5. Sub-task advancement uses plan's CoversSubTasks (no heuristics)
```

**Why main LLM for mapping?**
- Already has all tool definitions in KV cache — no duplicate injection
- Bigger model = better reasoning for tool selection and batching
- Plan in context helps LLM stay on track (feature, not noise)
- Secondary model better for quick isolated tasks (decomposition, summarization)

**Plan format (LLM outputs):**
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

**Sub-task advancement:** Each `PlannedToolCall` has a `CoversSubTasks` list. When a tool succeeds, the orchestrator advances exactly the sub-tasks the plan says that call covers. No string heuristics, tool-agnostic.

### Automated Test Framework

**Problem:** ECAssistant requires console interaction — can't be tested automatically.

**Solution:** Non-interactive test harness that pretends to be the user:

- `EGuiTestHarness` — implements `EGuiBase` without console. Captures all output to `StringBuilder`, provides scripted input via queue. Auto-approves tool calls.
- `TestRunner` — creates sandboxed dirs per test, sets up engine + tools + orchestrator, runs scenarios, evaluates assertions, reports pass/fail.
- `EcaTests` — 17 predefined test scenarios across 7 tiers (smoke, tool, multi-step, code, complex, edge, shell).

**Usage:**
```bash
dotnet run -- --test                    # Run all 17 tests
dotnet run -- --test --filter smoke_    # Run only smoke tests
dotnet run -- --test --filter tool_     # Run specific tier
dotnet run -- --test --model /path.gguf # Override model
```

**Test results (v10.17):** 17/17 passing (509s total, ~30s per test)

### Bug Fixes (v10.16.1-v10.16.2)
- `Console.KeyAvailable` guard — throws `InvalidOperationException` when no real console (test mode, redirected input). Wrapped in try/catch.
- `NativeLibraryConfig` static guard — LLamaSharp throws on second config attempt. Added `_nativeLibConfigured` static flag, one-time init.
- Sub-task advancement redesign — v10.15.8 advanced ALL sub-tasks on any success (too aggressive). v10.16.2: advance one per call (conservative). v10.17: advance based on plan's `CoversSubTasks` (precise).

## Git Tags (Session 2026-08-13)

```
pre-mac            — Pre-migration checkpoint (v10.15.8, Windows-only)
mac-safe           — Cross-platform, Mac-tested (v10.16.0)
v10.17-safe        — Two-phase planning + test framework (v10.17.0) ← CURRENT

Previous session tags:
v10.12.20-working  — Centralized truncation
v10.12.15-working  — Only </lm> stop tag
v10.11.2-working   — PowerShell error handling
v10.11.1-working   — ESC stop fix
v10.9.4-working    — Pre-<lm> baseline
```

**Status:** v10.17.0 — Two-phase planning (decompose → map → execute), automated test framework (17/17 tests passing), cross-platform (Windows + macOS). StepMapper eliminates sub-task advancement heuristics. EGuiTestHarness enables non-interactive testing. Ready for production testing on both platforms.