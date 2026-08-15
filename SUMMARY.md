# ECAssistant — Project Summary

**Updated:** 2026-08-15 (v10.23)
**Build:** 0 errors, 0 warnings
**Tests:** 940/940 passing

## What It Is

A local, offline AI coding assistant built in C# .NET 8 using LLamaSharp. Loads GGUF models from disk — no API calls, no cloud. Cross-platform (macOS + Windows). Uses `<lm>` container tag for response parsing with XML-style tool calling. Multi-step autonomous loops, dual memory, sliding context windows, self-correction, sub-agents, parallel tool execution.

## Project Structure (v10.23: Core + App Split)

Split into a **class library** (`ECAssistant.Core`) and a **console app** (`ECAssistant.App`). The Core DLL can be referenced by any .NET 8 project to embed the full ECAssistant engine — custom UI, custom tools, custom system prompt.

```
ECAssistant.sln
├── ECAssistant.Core.csproj    ← Class library (DLL) — engine, tools, session, memory, testing
├── ECAssistant.csproj         ← Console exe — only Program.cs, references Core
└── Tests/ECAssistant.Tests.csproj ← Tests, references Core
```

### Library Integration
- **`SessionBuilder`** — public class, initializes sessions with standard tools + memory
- **`EGuiBase`** — abstract UI base, implement for custom UIs (Avalonia, web, etc.)
- **`IOutputListener`** — implement to receive live session output
- **`EToolBase`** — subclass for custom domain-specific tools
- **`EAgentEngine.SystemPromptPath` / `SystemPromptText`** — injectable system prompts
- **`AgentSession`** — central hub: create, register tools, attach listeners, `Prompt()`

## Architecture (v10.23)

Full-screen alternate-buffer TUI (like nano/vim) with a **layer system**. `EGuiConsole` enters the terminal alternate screen buffer on startup, renders a fixed layout (output region / status bar / input line), and exits on shutdown — preserving the user's original terminal.

Clean separation: **Program.cs** (App — binder) creates **Session** (Core — headless) and **GUI** (Core — presentation). Everything inside the session communicates via `ISessionOutput` with `OutputState` enums. Zero UI/color/Console references in engine, tools, memory, or services.

Multi-session: each session runs headless with own KV cache, prompt queue, and runner thread. Sessions share model weights. Inference serialized via semaphore.

See `ARCHITECTURE.md` for full dependency flow and library integration guide.

## Key Stats
- **Projects:** 3 (Core library, App exe, Tests)
- **.cs files:** ~195 (excluding tests)
- **Test files:** ~65
- **Tests:** 940 passing, 0 failing
- **Tools:** 10 built-in (Shell, Git, CodeEditor, DotnetBuild, FileReader, WebSearch, WebFetch, FileResearch, BackgroundExec, SubAgent) + unlimited custom via `EToolBase`
- **Sessions:** Fully isolated — own engine, KV cache, tools, memory, output buffer, prompt queue

## File Structure
```
ECAssistant.sln               ← solution (3 projects)
ECAssistant.Core.csproj       ← class library (DLL)
ECAssistant.csproj            ← console exe (references Core)
Program.cs                    ← entry point, binder, command routing (App only)
ARCHITECTURE.md               ← dependency flow, layers, library integration guide
SUMMARY.md                    ← this file
EColor.cs                     ← ANSI color properties (UI layer only)
Config/                       ← strongly-typed config models + loader
Engine/                       ← EAgentEngine, Orchestrator, DecisionLoop, ParallelToolExecutor, SubAgentManager
  └── EAgentEngine.cs         ← SystemPromptPath + SystemPromptText (injectable, v10.23)
Interfaces/                   ← ITool, ILogger, IProcessRunner, IFileSystem, IHttpClient, IConfigProvider, IEngine, IInferenceEngine, etc.
Memory/                       ← EMemoryManager (persistent context cards)
Services/                     ← Logger, LlamaInferenceEngine, InMemoryVectorStore, adapters
Session/                      ← AgentSession, SessionManager, SessionBuilder, ISessionOutput, IOutputListener, ConsoleUiRenderer
  └── SessionBuilder.cs       ← NEW (v10.23): public API for library consumers
Tools/                        ← 10 tools across subfolders + EToolBase (subclass for custom)
UI/                           ← EGuiConsole, EGuiBase, IGuiLayer, SessionLayer, HelpLayer
Testing/                      ← TestRunner, MockEngine, EGuiTestHarness, EcaTests
Tests/                        ← unit + integration tests (UI, Session, Engine, Tools, Config, Memory, Services)
SystemPrompt.md               ← main system prompt
SystemPrompt.Mac.md           ← macOS-specific prompt
SystemPrompt.Windows.md       ← Windows-specific prompt
```

## Recent Changes (2026-08-15)
- **v10.23: Core + App split** — ECAssistant.Core.csproj (class library) + ECAssistant.csproj (console exe). All engine, tools, session, memory code in Core DLL. Program.cs is the only file in App.
- **SessionBuilder** — public class extracted from old `Program.InitSessionAsync`. Library consumers call `BuildAsync(session)` to get standard tools + memory + secondary model + sub-agents. Flags to skip built-in tools (`RegisterBuiltInTools = false`).
- **Injectable system prompt** — `EAgentEngine.SystemPromptPath` (file path override) and `SystemPromptText` (direct string injection). Library consumers can set custom prompts without file I/O.
- **TestRunner decoupled** — `TestRunner.TestGui` static property replaces `Program.Gui` coupling. Testing code stays in Core.
- **Solution file** — `ECAssistant.sln` ties Core + App + Tests together.
- **940 tests passing** — all green after split, 0 errors, 0 warnings.
- **Previous:** Layer system, session/UI rewrite with ISessionOutput, headless engine, scrollback, resize detection