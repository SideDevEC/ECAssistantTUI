# ECAssistant — Project Summary

**Updated:** 2026-08-15
**Build:** 0 errors, 0 warnings
**Tests:** 940/940 passing

## What It Is

A local, offline AI coding assistant built in C# .NET 8 using LLamaSharp. Loads GGUF models from disk — no API calls, no cloud. Cross-platform (macOS + Windows). Uses `<lm>` container tag for response parsing with XML-style tool calling. Multi-step autonomous loops, dual memory, sliding context windows, self-correction, sub-agents, parallel tool execution.

## Architecture (v11.4 — 2026-08-15)

Full-screen alternate-buffer TUI (like nano/vim) with a **layer system**. `EGuiConsole` enters the terminal alternate screen buffer on startup (buffering all output first, then flushing in one paint — no blank gap), renders a fixed layout (output region / status bar / input line), and exits on shutdown — preserving the user's original terminal.

**Layer stack:** SessionLayer (base, layer 1) → HelpLayer → future layers. Each layer owns the full screen. Push/pop via `IGuiLayer` interface. Keys routed to active layer. Output buffered while layer is active, restored on pop. Help closes on Enter only.

All output stored in internal buffer and scrollable with ↑/↓/PageUp/PageDown/Home/End. Dirty rendering only repaints changed regions. 200ms resize watcher auto-detects terminal size changes. Steady block cursor at input line.

Clean separation: **Program.cs** (binder) creates **Session** (headless) and **GUI** (presentation). Everything inside the session communicates via `ISessionOutput` with `OutputState` enums. Zero UI/color/Console references in engine, tools, memory, or services. Only `EGuiConsole` touches the terminal.

Multi-session: each session runs headless with own KV cache, prompt queue, and runner thread. Sessions share model weights. Inference serialized via semaphore. Switching sessions clears screen and renders full output history from JSONL. Prompt queue is FIFO — queued prompts process in order after current execution.

See `ARCHITECTURE.md` for full dependency flow.

## Key Stats
- **.cs files:** ~195 (excluding tests)
- **Test files:** ~65
- **Tests:** 940 passing, 0 failing
- **Tools:** 10 (Shell, Git, CodeEditor, DotnetBuild, FileReader, WebSearch, WebFetch, FileResearch, BackgroundExec, SubAgent)
- **Sessions:** Fully isolated — own engine, KV cache, tools, memory, output buffer, prompt queue
- **Layers:** SessionLayer (base), HelpLayer — extensible via IGuiLayer

## File Structure
```
Program.cs              — entry point, binder, command routing
ARCHITECTURE.md         — dependency flow and layer descriptions
SUMMARY.md              — this file
EColor.cs               — ANSI color properties (UI layer only)
Config/                 — strongly-typed config models + loader
Engine/                 — EAgentEngine, Orchestrator, DecisionLoop, ParallelToolExecutor, SubAgentManager
Interfaces/             — ITool, ILogger, IProcessRunner, IFileSystem, IHttpClient, IConfigProvider
Memory/                 — EMemoryManager (persistent context cards)
Services/               — Logger, LlamaInferenceEngine, InMemoryVectorStore, adapters
Session/                — AgentSession, SessionManager, ISessionOutput, IOutputListener, ConsoleUiRenderer, LoadingIndicator
Tools/                  — 10 tools across subfolders
UI/                     — EGuiConsole, EGuiBase, IGuiLayer, SessionLayer, HelpLayer
Tests/                  — unit + integration tests (UI, Session, Engine, Tools, Config, Memory, Services)
SystemPrompt.md         — main system prompt
SystemPrompt.Mac.md     — macOS-specific prompt
SystemPrompt.Windows.md — Windows-specific prompt
```

## Recent Changes (2026-08-15)
- **Layer system:** IGuiLayer interface, SessionLayer (base), HelpLayer (full-screen, Enter to close). Push/pop stack. Keys routed to active layer. Output buffered during layer mode.
- **Session as layer:** SessionLayer is layer 1, always at bottom of stack. PopLayer peeks next layer down.
- **Session switch renders history:** ClearCanvas + ReadOutputHistory + RenderHistory on switch. Previously old session's output stayed on screen.
- **Startup buffering:** Output queued in _startupBuffer, InitConsole() flushes all at once. No blank gap on launch.
- **Scrollback navigation:** All output in _outputLines, scrollable with ↑/↓/PageUp/PageDown/Home/End. Status bar shows position. Long lines wrapped.
- **Resize detection:** 200ms background timer auto-detects terminal size change, immediate full repaint.
- **Steady cursor:** Block cursor (reverse-video space) at input line.
- **ClearCanvas:** Added to EGuiBase + EGuiConsole + EGuiTestHarness. `clear` command.
- **LoadingIndicator:** Uses status bar instead of \r animation. No more 0m/2m artifacts.
- **StripAnsi fix:** Proper CSI (\x1b[) and OSC (\x1b]) sequence parsing. Was cutting escape sequences early.
- **Windows P/Invoke:** Added [SupportedOSPlatform("windows")] attributes — builds on Linux.
- **Removed CI workflow:** Limited value for solo desktop app.
- **16 new tests:** LayerStackTests (9), SessionQueueTests (7). 940 total.
- **Previous:** Session/UI rewrite with ISessionOutput, headless engine, warning cleanup, test fixes