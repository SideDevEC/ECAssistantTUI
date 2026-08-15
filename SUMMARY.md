# ECAssistant — Project Summary

**Updated:** 2026-08-16 (v10.24.2)
**Build:** 0 errors, 0 warnings
**Tests:** 907/907 passing

## What It Is

A local, offline AI coding assistant built in C# .NET 8 using LLamaSharp. Loads GGUF models from disk — no API calls, no cloud. Cross-platform (macOS + Windows). Uses `<lm>` container tag for response parsing with XML-style tool calling. Multi-step autonomous loops, dual memory, sliding context windows, self-correction, sub-agents, parallel tool execution.

## Project Structure

Split into a **class library** (`ECAssistant.Core`) and a **console app** (`ECAssistant.App`). The Core DLL can be referenced by any .NET 8 project to embed the full ECAssistant engine.

```
ECAssistant.sln
├── ECAssistant.Core.csproj    ← Class library (DLL) — engine, tools, session, memory, testing
├── ECAssistant.csproj         ← Console exe — only Program.cs, references Core
└── Tests/ECAssistant.Tests.csproj ← Tests, references Core
```

### Library Integration
- **`AgentConfigBuilder`** — fluent config, JSON-first: generates `appsettings.json` on first run, loads it after
- **`SystemPromptBuilder`** — required `<lm>` tag rules + auto-detect OS + domain context
- **`SessionBuilder`** — initializes sessions with standard tools (or skip with `RegisterBuiltInTools = false`)
- **`EGuiBase`** — abstract UI base, implement for custom UIs (Avalonia, web, etc.)
- **`IOutputListener`** — implement to receive live session output
- **`EToolBase`** — subclass for custom domain-specific tools
- **`EToolResult`** — `Success(name, output)` / `Failure(name, error)` with `.Succeeded`, `.Output`, `.Error`
- **`AgentSession`** — central hub: create, register tools, attach listeners, `Prompt()`

### Tool System (v10.24: Unified on EToolBase)
- All tools extend `EToolBase` — `ITool` interface deleted, `ToolAdapter` deleted
- `ExecuteAsync(Dictionary<string, string?>)` — orchestrator parses XML tags to dict
- `EToolResult` return type — `.Succeeded`, `.Output`, `.Error`
- `GetConfigSection()` — every tool declares its default config (at minimum `{ enabled = true }`)
- `IsEnabled` — every tool has enabled/disabled flag
- `AgentSession.RegisterTool()` auto-registers missing tool config sections to `appsettings.json`
- `ToolsConfig` is `Dictionary<string, JsonElement>` — dynamic, any tool adds its section
- 10 built-in tools: Shell, Git, CodeEditor, DotnetBuild, FileReader, WebSearch, WebFetch, FileResearch, BackgroundExec, SubAgent

### Config Flow (JSON is source of truth)
1. **First run:** `AgentConfigBuilder.Build()` generates `appsettings.json` in `<workingDir>/eca-data/`
2. **Subsequent runs:** loads existing JSON — code values ignored
3. **End users** edit `appsettings.json` to change settings — they never see code
4. **Tool configs** auto-added to `tools` section on first registration
5. Working directory: always `<given_path>/eca-data/` (default: `./eca-data/`)

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

## Key Stats
- **Projects:** 3 (Core library, App exe, Tests)
- **.cs files:** ~195 (excluding tests)
- **Test files:** ~63
- **Tests:** 907 passing, 0 failing
- **Tools:** 10 built-in + unlimited custom via `EToolBase`
- **Sessions:** Fully isolated — own engine, KV cache, tools, memory, output buffer

## File Structure
```
ECAssistant.sln               ← solution (3 projects)
ECAssistant.Core.csproj       ← class library (DLL)
ECAssistant.csproj            ← console exe (references Core)
Program.cs                    ← entry point (App only)
ARCHITECTURE.md               ← full architecture + library integration guide
SUMMARY.md                    ← this file
SystemPromptBuilder.cs        ← fluent system prompt builder with <lm> tag rules + OS detect
Config/
  ├── AgentConfigBuilder.cs   ← fluent config builder, JSON-first
  ├── EAgentConfig.cs         ← Tools is Dictionary<string, JsonElement> (dynamic)
  └── ConfigLoader.cs         ← JSON deserialization
Engine/
  ├── EAgentEngine.cs         ← SystemPromptPath + SystemPromptText + ModelPath properties
  ├── SubAgentManager.cs      ← config injected, no hardcoded disk reads
  ├── Orchestrator.cs         ← parses <toolcall> to Dictionary, dispatches to EToolBase
  └── ...
Interfaces/                   ← IProcessRunner, IFileSystem, IHttpClient, ILogger, etc.
Memory/                       ← EMemoryManager
Services/                     ← Logger, LlamaInferenceEngine, ConfigProvider, adapters
Session/
  ├── SessionBuilder.cs       ← public API for library consumers
  ├── AgentSession.cs         ← auto-registers tool config on RegisterTool()
  └── ...
Tools/
  ├── EToolBase.cs            ← abstract base: GetConfigSection(), IsEnabled, ReadCfg<T>()
  ├── EToolResult.cs          ← Success/Failure record
  ├── EShell/EGit/ECode/...   ← 9 built-in tools (all extend EToolBase)
  └── SubAgent/ESubAgentTool.cs
UI/                           ← EGuiConsole, EGuiBase, IGuiLayer
Testing/                      ← TestRunner, MockEngine, EGuiTestHarness
Tests/                        ← unit + integration tests (907 total)
SystemPrompt.md               ← main system prompt (console app)
SystemPrompt.Mac.md           ← macOS-specific prompt
SystemPrompt.Windows.md       ← Windows-specific prompt
```

## Recent Changes (2026-08-15/16)
- **v10.24.2: All tests passing** — 907/907, ITool removed, EToolBase unified
- **v10.24.1: Remove ITool** — deleted ITool interface, ToolAdapter, updated all 18 test files
- **v10.24: Dynamic tool config + ITool→EToolBase migration**
  - `ToolsConfig` → `Dictionary<string, JsonElement>` (dynamic)
  - `EToolBase.GetConfigSection()` + `IsEnabled` on every tool
  - `AgentConfigBuilder.Update()` persists new tool configs
  - `AgentSession.RegisterTool()` auto-registers missing tool sections
  - All 9 tools: `ExecuteAsync(string)` → `ExecuteAsync(Dictionary)`, return `EToolResult`
  - Old `ToolPowerShellConfig`, `ToolFileResearchConfig`, `ToolsConfig` deleted
- **v10.23.3: App uses AgentConfigBuilder** — dogfooding Core's library API
- **v10.23.2: JSON-first config** — code seeds initial JSON, after that JSON is source of truth
- **v10.23.1: Clean library** — no hardcoded disk reads, fluent config + prompt builders
- **v10.23: Core + App split** — class library + console exe, `SessionBuilder`, injectable prompts
- **Previous:** Layer system, session/UI rewrite, headless engine