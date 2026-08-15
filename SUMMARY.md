# ECAssistant — Project Summary

**Updated:** 2026-08-15
**Build:** 0 errors, 0 warnings
**Tests:** 892/892 passing

## What It Is

A local, offline AI coding assistant built in C# .NET 8 using LLamaSharp. Loads GGUF models from disk — no API calls, no cloud. Cross-platform (macOS + Windows). Uses `<lm>` container tag for response parsing with XML-style tool calling. Multi-step autonomous loops, dual memory, sliding context windows, self-correction, sub-agents, parallel tool execution.

## Architecture (v11.2 — 2026-08-15)

Clean separation: **Program.cs** (binder) creates **Session** (headless) and **GUI** (presentation). Everything inside the session communicates via `ISessionOutput` with `OutputState` enums. Zero UI/color/Console references in engine, tools, memory, or services. Only `EGuiConsole` touches the terminal.

See `ARCHITECTURE.md` for full dependency flow.

## Key Stats
- **.cs files:** ~190 (excluding tests)
- **Test files:** ~60
- **Tests:** 892 passing, 0 failing
- **Tools:** 10 (Shell, Git, CodeEditor, DotnetBuild, FileReader, WebSearch, WebFetch, FileResearch, BackgroundExec, SubAgent)
- **Sessions:** Fully isolated — own engine, KV cache, tools, memory, output buffer, prompt queue

## File Structure
```
Program.cs              — entry point, binder
ARCHITECTURE.md         — dependency flow and layer descriptions
SUMMARY.md              — this file
EColor.cs               — ANSI color properties (UI layer only)
StringUtil.cs           — Truncate() utility (replaces EGuiBase.Truncate)
Config/                 — strongly-typed config models + loader
Engine/                 — EAgentEngine, Orchestrator, DecisionLoop, ParallelToolExecutor, SubAgentManager
Interfaces/             — ITool, ILogger, IProcessRunner, IFileSystem, IHttpClient, IConfigProvider
Memory/                 — EMemoryManager (persistent context cards)
Services/               — Logger, LlamaInferenceEngine, InMemoryVectorStore, adapters
Session/                — AgentSession, ISessionOutput, IOutputListener, OutputTypes, ConsoleUiRenderer
Tools/                   — 10 tools across subfolders
UI/                     — EGuiConsole, EGuiBase (only terminal I/O)
Tests/                  — unit + integration tests
SystemPrompt.md         — main system prompt
SystemPrompt.Mac.md     — macOS-specific prompt
SystemPrompt.Windows.md — Windows-specific prompt
```

## Recent Refactors (2026-08-15)
- **Session/UI rewrite:** ISessionOutput with StartStream/Write/StopStream, RequestApproval (blocks until listener responds), IOutputListener, always-visible `>` prompt
- **Headless engine:** Removed ALL IColorFormatter/EColor/EGuiBase/Console deps from engine, tools, memory, config, logger. Everything communicates via ISessionOutput states only
- **Warning cleanup:** 21 build warnings → 0 (null dereferences, unused vars, unawaited async, OSPlatform comparison)
- **Test fixes:** All 12 pre-existing test failures fixed (EDotnetBuildTool assertions, ToolAdapter XML format, ConfigLoader case-insensitive JSON)
- **Deleted:** IColorFormatter.cs, ColorFormatter.cs, ColorFormatterTests.cs, 7 outdated .md files