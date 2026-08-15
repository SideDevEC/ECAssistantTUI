# ECAssistant — Project Summary

**Updated:** 2026-08-15
**Build:** 0 errors, 0 warnings
**Tests:** 892/892 passing

## What It Is

A local, offline AI coding assistant built in C# .NET 8 using LLamaSharp. Loads GGUF models from disk — no API calls, no cloud. Cross-platform (macOS + Windows). Uses `<lm>` container tag for response parsing with XML-style tool calling. Multi-step autonomous loops, dual memory, sliding context windows, self-correction, sub-agents, parallel tool execution.

## Architecture (v11.3 — 2026-08-15)

Full-screen alternate-buffer TUI (like nano/vim). `EGuiConsole` enters the terminal alternate screen buffer on startup, renders a fixed layout (output region / status bar / input line), and exits on shutdown — preserving the user's original terminal. All output is stored in an internal buffer and scrollable with ↑/↓/PageUp/PageDown/Home/End. Dirty rendering only repaints changed regions. No more prompt disappearing, input/output interleaving, or flashing.

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
UI/                     — EGuiConsole (full-screen TUI), EGuiBase (abstract base)
Tests/                  — unit + integration tests
SystemPrompt.md         — main system prompt
SystemPrompt.Mac.md     — macOS-specific prompt
SystemPrompt.Windows.md — Windows-specific prompt
```

## Recent Changes (2026-08-15)
- **Full-screen TUI rewrite:** EGuiConsole now uses alternate screen buffer with fixed layout — output region, status bar, input line at fixed rows. No more clear/reprint prompt gymnastics.
- **Scrollback navigation:** All output stored in `_outputLines`, scrollable with ↑/↓/PageUp/PageDown/Home/End. Status bar shows scroll position. Long lines wrapped not truncated.
- **ClearCanvas():** Added to EGuiBase + EGuiConsole + EGuiTestHarness. `clear` command wired up in Program.cs.
- **Dirty rendering:** Only repaints changed regions (_outputDirty, _statusDirty, _inputDirty, _fullRepaint).
- **Resize handling:** CheckResize() detects terminal size changes, triggers full repaint.
- **ShutdownConsole():** Called on exit to leave alternate buffer and restore terminal.
- **Previous:** Session/UI rewrite with ISessionOutput, headless engine (removed ALL UI/color deps), warning cleanup (21→0), test fixes (12→0), deleted IColorFormatter/ColorFormatter