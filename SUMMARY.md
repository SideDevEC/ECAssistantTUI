# ECAssistant — Project Summary

**Updated:** 2026-08-15 (v10.23.1)
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

### Library Integration — No appsettings.json needed
- **`AgentConfigBuilder`** — fluent config: `.WithModel().ContextSize().GpuLayers().Temperature().Build()`
- **`SystemPromptBuilder`** — required `<lm>` tag rules + domain context: `.WithAgentName().WithDescription().WithCustomRules().Build()`
- **`SessionBuilder`** — initializes sessions with standard tools (or skip with `RegisterBuiltInTools = false`)
- **`EGuiBase`** — abstract UI base, implement for custom UIs (Avalonia, web, etc.)
- **`IOutputListener`** — implement to receive live session output
- **`EToolBase`** — subclass for custom domain-specific tools
- **`EAgentEngine.SystemPromptPath` / `SystemPromptText`** — injectable system prompts
- **`AgentSession`** — central hub: create, register tools, attach listeners, `Prompt()`

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
SystemPromptBuilder.cs        ← NEW (v10.23.1): fluent system prompt builder with <lm> tag rules
Config/
  ├── AgentConfigBuilder.cs   ← NEW (v10.23.1): fluent config builder for library consumers
  ├── EAgentConfig.cs         ← RootPath default: "." (v10.23.1)
  └── ...                     ← config models + loader
Engine/
  ├── EAgentEngine.cs         ← SystemPromptPath + SystemPromptText + ModelPath properties
  ├── SubAgentManager.cs      ← config injected, no hardcoded ~/ECAssistant/ reads (v10.23.1)
  └── ...                     ← Orchestrator, DecisionLoop, ParallelToolExecutor, etc.
Interfaces/                   ← ITool, ILogger, IProcessRunner, IFileSystem, IHttpClient, IConfigProvider, etc.
Memory/                       ← EMemoryManager (persistent context cards)
Services/
  ├── ConfigProvider.cs       ← supports preloaded EAgentConfig (no file I/O) (v10.23.1)
  └── ...                     ← Logger, LlamaInferenceEngine, adapters
Session/
  ├── SessionBuilder.cs       ← public API for library consumers
  ├── AgentSession.cs         ← accepts optional EAgentConfig for passing to orchestrator (v10.23.1)
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
- **v10.23.1: Clean library** — removed all hardcoded `~/ECAssistant/` disk reads from Core
  - `SubAgentManager`: config + model path injected via constructor (was reading `~/ECAssistant/appsettings.json`)
  - `ConfigProvider`: new constructor taking `EAgentConfig` directly (no file I/O)
  - `EAgentConfig.RootPath`: default `"."` instead of `"ECAssistant"`
  - `AgentConfigBuilder`: fluent config API — `.WithModel().ContextSize().GpuLayers().Temperature().Build()`
  - `SystemPromptBuilder`: fluent system prompt — required `<lm>` tag rules + domain context
  - `EAgentEngine.ModelPath`: public property for SubAgentManager access
  - `AgentSession`: accepts optional `EAgentConfig`, passes to orchestrator → SubAgentManager
  - `Orchestrator`: accepts optional `EAgentConfig`, passes to SubAgentManager
- **v10.23: Core + App split** — ECAssistant.Core.csproj (class library) + ECAssistant.csproj (console exe)
  - `SessionBuilder` — public class for library consumers
  - Injectable system prompt (`SystemPromptPath` / `SystemPromptText`)
  - `TestRunner` decoupled from `Program.Gui`
  - 940 tests passing
- **Previous:** Layer system, session/UI rewrite with ISessionOutput, headless engine