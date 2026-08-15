# ECAssistant — Project Summary

**Updated:** 2026-08-15 (v10.23.2)
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
- **`AgentConfigBuilder`** — fluent config, JSON-first: generates `appsettings.json` on first run, loads it after. JSON is source of truth.
- **`SystemPromptBuilder`** — required `<lm>` tag rules + domain context (fluent)
- **`SessionBuilder`** — initializes sessions with standard tools (or skip with `RegisterBuiltInTools = false`)
- **`EGuiBase`** — abstract UI base, implement for custom UIs (Avalonia, web, etc.)
- **`IOutputListener`** — implement to receive live session output
- **`EToolBase`** — subclass for custom domain-specific tools
- **`EAgentEngine.SystemPromptPath` / `SystemPromptText`** — injectable system prompts
- **`AgentSession`** — central hub: create, register tools, attach listeners, `Prompt()`

### Config Flow (v10.23.2: JSON is source of truth)
1. **First run:** `AgentConfigBuilder.Build()` generates `appsettings.json` in `<workingDir>/eca-data/` with code values + defaults
2. **Subsequent runs:** loads existing JSON — code values are ignored
3. **End users** edit `appsettings.json` to change settings (model path, temperature, context size, etc.) — they never see code
4. Working directory: always `<given_path>/eca-data/` (default: `./eca-data/`)

### On-disk layout (library consumer)
```
./eca-data/
├── appsettings.json          ← generated on first run, editable by end users
├── .sessions/main/
│   ├── transcript.json
│   └── ui_output.jsonl
├── Memory/
└── vecmem/
```

### No Disk Dependencies
- `SubAgentManager` receives config via constructor injection — no `~/ECAssistant/` reads
- `ConfigProvider` supports preloaded `EAgentConfig` — no file I/O for library consumers
- `EAgentConfig.RootPath` defaults to `"."` (not `"ECAssistant"`)
- `<lm>` tag format rules compiled into DLL — always present via `SystemPromptBuilder`

## Key Stats
- **Projects:** 3 (Core library, App exe, Tests)
- **.cs files:** ~197 (excluding tests)
- **Test files:** ~65
- **Tests:** 940 passing, 0 failing
- **Tools:** 10 built-in + unlimited custom via `EToolBase`
- **Sessions:** Fully isolated — own engine, KV cache, tools, memory, output buffer, prompt queue

## File Structure
```
ECAssistant.sln               ← solution (3 projects)
ECAssistant.Core.csproj       ← class library (DLL)
ECAssistant.csproj            ← console exe (references Core)
Program.cs                    ← entry point, binder, command routing (App only)
ARCHITECTURE.md               ← dependency flow, layers, library integration guide
SUMMARY.md                    ← this file
SystemPromptBuilder.cs        ← fluent system prompt builder with <lm> tag rules
Config/
  ├── AgentConfigBuilder.cs   ← fluent config builder, JSON-first (generates/loads appsettings.json)
  ├── EAgentConfig.cs         ← RootPath default: "."
  └── ...                     ← config models + loader
Engine/
  ├── EAgentEngine.cs         ← SystemPromptPath + SystemPromptText + ModelPath properties
  ├── SubAgentManager.cs      ← config injected, no hardcoded ~/ECAssistant/ reads
  └── ...                     ← Orchestrator, DecisionLoop, ParallelToolExecutor, etc.
Interfaces/                   ← ITool, ILogger, IProcessRunner, IFileSystem, IHttpClient, IConfigProvider, etc.
Memory/                       ← EMemoryManager (persistent context cards)
Services/
  ├── ConfigProvider.cs       ← supports preloaded EAgentConfig (no file I/O)
  └── ...                     ← Logger, LlamaInferenceEngine, adapters
Session/
  ├── SessionBuilder.cs       ← public API for library consumers
  ├── AgentSession.cs         ← accepts optional EAgentConfig for passing to orchestrator
  └── ...                     ← SessionManager, ISessionOutput, IOutputListener, ConsoleUiRenderer
Tools/                        ← 10 tools across subfolders + EToolBase (subclass for custom)
UI/                           ← EGuiConsole, EGuiBase, IGuiLayer, SessionLayer, HelpLayer
Testing/                      ← TestRunner, MockEngine, EGuiTestHarness, EcaTests
Tests/                        ← unit + integration tests
SystemPrompt.md               ← main system prompt (console app)
SystemPrompt.Mac.md           ← macOS-specific prompt
SystemPrompt.Windows.md       ← Windows-specific prompt
```

## Recent Changes (2026-08-15)
- **v10.23.2: JSON-first config** — `AgentConfigBuilder.Build()` generates `appsettings.json` on first run, loads it on subsequent runs. JSON is source of truth — code values only seed initial file. Working dir always appends `eca-data` to given path. End users edit JSON to change settings without touching code.
- **v10.23.1: Clean library** — removed all hardcoded `~/ECAssistant/` disk reads from Core. `SubAgentManager` config injected. `ConfigProvider` supports preloaded config. `AgentConfigBuilder` + `SystemPromptBuilder` added. `EAgentConfig.RootPath` defaults to `"."`.
- **v10.23: Core + App split** — `ECAssistant.Core.csproj` (class library) + `ECAssistant.csproj` (console exe). `SessionBuilder` public API. Injectable system prompt. `TestRunner` decoupled from `Program.Gui`. 940 tests passing.
- **Previous:** Layer system, session/UI rewrite with ISessionOutput, headless engine