# ECAssistant TUI — Architecture

**Updated:** 2026-08-26 (v12.0 — thread-safe approval prompt, no-animation loading indicator, LDC compliance)
**Build:** 0 errors, 0 warnings
**Tests:** 66/66 passing
**Namespace:** `ECAssistant.TUI.*`

## Overview

ECAssistantTUI is a full-screen terminal UI library for a local, offline AI coding assistant. It is a **class library** (not an executable) that wires into `ECAssistant.Core.dll`. Any .NET 8 app can reference it — the standalone `ECAssistantConsole` launcher, or future GUI hosts like ECSQL's Avalonia terminal pane.

The architecture follows a strict three-layer separation:

1. **Controller** — application logic, layer-switching commands, session/layer lifecycle
2. **EGuiConsole** — terminal rendering engine, input reading, delta rendering
3. **BaseLayer** — per-screen state: output buffers, scroll position, status bar

Nobody knows about things above them. Dependencies flow strictly top-down.

## Project Structure

```
ECAssistantTUI/
├── ECAssistant.TUI.csproj          ← Class library
│     OutputType=Library, AssemblyName=ECAssistant.TUI
│     RootNamespace=ECAssistant.TUI
│     References: ECAssistant.Core.dll (HintPath: lib/)
│     No LLamaSharp packages — replaced with Microsoft.Extensions.Logging.Abstractions
│     InternalsVisibleTo: ECAssistant.TUI.Tests
│
└── Tests/ECAssistant.TUI.Tests.csproj
      ProjectReference → ECAssistant.TUI.csproj
      Reference → ECAssistant.Core.dll
      66 tests
```

## Files (v11.1)

```
Controller/
└── AppController.cs                ← Application logic, layer-switching, session management (v11.2: StopAll/DeleteSession/List/Rename migration)
UI/
├── IGuiConsole.cs                  ← Interface for terminal injection (hosts implement this)
├── EGuiConsole.cs                  ← Terminal engine (implements IGuiConsole)
├── BaseLayer.cs                    ← Abstract layer: output buffer, scroll, status, ANSI helpers
├── SessionLayer.cs                 ← One per session, owns output buffer, handles session commands (v11.2: inline context status)
├── StartupLayer.cs                 ← Always-present home screen, boot log, app status
├── HelpLayer.cs                    ← Help overlay
├── ConfigLayer.cs                  ← Config inspection overlay (v11.2: LLM Provider section)
├── LoadingIndicator.cs             ← Animated loading dots
└── ConsoleUiRenderer.cs            ← Bridges Core IOutputListener → SessionLayer buffer
```

## IGuiConsole Interface (v11.1)

Enables external apps to host ECAssistant's TUI without using System.Console directly:

```csharp
public interface IGuiConsole
{
    void SetCallbacks(Action<string> onPrompt, Action onEscape);
    void SetActiveLayer(BaseLayer? layer);
    void InitConsole();
    void ShutdownConsole();
    void Quit();
    bool IsQuitRequested { get; }
    void RequestRepaint();
    int ScreenWidth { get; }
    int ScreenHeight { get; }
    // + output methods: WriteLine, WriteLineColored, BlankLine, etc.
}
```

- `EGuiConsole` implements it for standalone terminal
- ECSQL (or other hosts) implement it with a PTY-backed Avalonia control
- `AppController` accepts `IGuiConsole` via constructor — fully injectable

## AppController Constructors (v11.1)

```csharp
// Without external tools — standalone use
new AppController(console, config, modelPath, workingDir, userConfigDir, logger)

// With external tools — host injects custom tools
new AppController(console, config, modelPath, workingDir, userConfigDir, logger, externalTools)
```

Both call `SessionBuilder.BuildAsync(session, _externalTools)` which registers external tools first, then native tools.

## Dependency Flow

```
Host app (Console, ECSQL, etc.)
  │
  │ creates IGuiConsole implementation (EGuiConsole or custom)
  │ creates AppController(console, config, ..., externalTools?)
  │ calls controller.RunAsync()
  ▼
AppController
  ├── creates → Dictionary<string, BaseLayer> (owns all layers)
  ├── holds  → IGuiConsole (injected)
  ├── holds  → SessionManager (Core)
  ├── holds  → List<EToolBase>? externalTools (injected)
  ├── creates SessionBuilder with ExternalTools
  └── calls builder.BuildAsync(session, _externalTools)
  ▼
EGuiConsole (implements IGuiConsole)
  ├── owns: terminal (alt buffer, cursor, ANSI, dimensions)
  ├── owns: input buffer, delta rendering
  └── calls back: controller.OnPrompt(), controller.OnEscapePressed()
  ▼
BaseLayer (abstract)
  ├── owns: OutputLines[] buffer, ScrollOffset, StatusBar
  ├── holds: IGuiConsole reference (set via BindToConsole)
  └── SessionLayer, StartupLayer, HelpLayer, ConfigLayer (concrete)
```

## Command Routing

```
User types input → Enter → Controller.OnPrompt(input)
  │
  ├── /help, /home, /config, /quit, /session, /session-new
  │     → Controller handles directly (layer switching / shutdown)
  │
  └── Everything else → activeLayer.ProcessInput(input)
        ├── Returns true → handled by layer
        └── Returns false → Controller.HandleSessionManagerCommand(input)
```

## Key Constraints

- `Console.Write/WriteLine` ONLY in `EGuiConsole`
- `EColor` used ONLY by layers and renderers (not in Core)
- TUI has zero LLamaSharp dependencies — removed entirely from `ECAssistant.TUI.csproj` (replaced with `Microsoft.Extensions.Logging.Abstractions`)
- TUI has zero external file dependencies (system prompts + config in Core DLL)
- `AppController` only interacts via `IGuiConsole` — no direct `Console.*` calls
- `EGuiConsole` has no application logic — renders and forwards input
- Layers have no knowledge of each other or the controller
- Controller is the only class that knows about all three (console + layers + Core)

## Build Order

```
1. Build ECAssistantCore.sln → produces ECAssistant.Core.dll
2. Copy DLL to ECAssistantTUI/lib/
3. Build ECAssistant.TUI.csproj → produces ECAssistant.TUI.dll
```

## v11.7 — RunAsync Refactor + Tool Approval

- **RunAsync simplified** — extracted into 3 clean steps: `InitializeAppAsync()` → input loop → `ShutdownAppAsync()`. Was 150 lines inline, now each method has a single responsibility.
- **Tool approval prompting** — `ConsoleUiRenderer.OnRequestApproval` implemented via `Func<string, bool>` callback (was `NotImplementedException`). `AppController.PromptApproval()` shows `⚠ APPROVAL REQUIRED` + tool name + args, prompts y/n via `PromptColored`.
- **Approval callback** wired into all `ConsoleUiRenderer` instances (both disk-loaded and runtime-created sessions).

## v11.6 — Idle Watchdog + Shutdown Wiring

- **`InitializeAsync`** — `AppController.RunAsync` now calls `_sessionManager.InitializeAsync()` before `LoadSessionsFromDiskAsync()` (registers with LLM server, gets clientId, starts heartbeat). Without this, all `/eca/*` requests had no `X-Client-Id` header.
- **Shutdown wiring** — `AppController` now calls `await _sessionManager.DisposeAsync()` on quit (was missing). This triggers `DisconnectAsync()` + `ServerLauncher.StopServerAsync()` → POST `/eca/shutdown` → server winds down if last client.
- **Idle watchdog** — `StartIdleWatchdog(15)` starts a 60s timer; after 15 min user inactivity → disconnects from server (frees VRAM). `MarkUserActivity()` called on every input in the input loop; if idle-disconnected, triggers `ReconnectAfterIdleAsync()`.
- **Error handling** — `InitializeAsync` wrapped in try/catch with helpful error message ("Start the LLM server first...").

## v11.2 — Core/LLM Split Migration (v10.31)

The TUI was updated to match the new Core HTTP-based engine surface:

- **`AppController`** (6 call sites migrated to the new `SessionManager` API):
   - `StopAllAsync()` → `StopAll()` (sync shutdown)
   - `GetStatusReport()` → inline session list with a `→` active marker
   - `GetByIndex(idx)` → `List()[idx - 1]` (Stop/Close/Peek/Rename/Info paths)
   - `CloseSessionAsync(key)` → `DeleteSession(key)`
   - `RenameSession(idx, label)` → `session.Rename(label)`
- **`ConfigLayer`** — removed `GPU Layers` + `Threads` (moved to LLM server's `llm-server.json`). Added an **LLM Provider** section: `Mode`, `Endpoint`, `Model ID`, `Embedding Model` (local), `Auto-Start`/`Heartbeat` (local), `API Key` (remote, masked).
- **`SessionLayer`** — replaced the removed `Engine.ContextStatusSummary` with an inline format: `tokens used/max (pct%) | KV: MB | prefilled/cold`.
- **`ECAssistant.TUI.csproj`** — removed all LLamaSharp packages + `System.Text.Json`; added `Microsoft.Extensions.Logging.Abstractions`.
- **Build** — fresh Core + TUI DLLs are copied into `lib/` after building Core.

## Test Summary (66 tests)

| File | Tests | What |
|---|---|---|
| `BaseLayerAnsiTests.cs` | 15 | StripAnsi, TruncateAnsi, WrapLine |
| `BaseLayerBufferTests.cs` | 10 | AddOutputLine, multi-line, scroll reset, clear |
| `LayerTests.cs` | 21 | SessionLayer, HelpLayer, StartupLayer, ConfigLayer, scroll, live stream |
| `ConsoleUiRendererTests.cs` | 16 | OnOutput tags, stream, RenderHistory, writes to SessionLayer |
## First-Run Setup Wizard (2026-08-27)

- `Controller/FirstRunWizard.cs` — shown by `AppController.InitializeAppAsync` before session init when `FirstRunDetector` reports no usable models
- Grouped numbered list (chat / vision / embedding, ★ = recommended), picks `1,3` / `a` (all recommended) / Enter to skip
- Downloads via Core `ModelInstallerService` with per-percent progress bars; results merged into `llm-server.json` automatically
- Catalog source: `model-catalog.json` in app root (auto-created with defaults, user-editable)
- All failures are non-fatal — setup is wrapped in try/catch and never blocks startup

## Changelog — 2026-08-27 (Installer Wizard + /menu)

- **FirstRunWizard**: local/remote AI choice; remote = endpoint/key/model/embedding-model + live connection test, key encrypted via SecureKeyStore.SetKey (keyfile: ref); local = catalog + internet/disk pre-flight, retry ×3, GPU preference → gpu_layers, memory estimates, embeddings ensure, post-install test, model removal
- **AppController**: `/reinstall` (warn → stop server, delete keys/config, models kept → wizard), `/menu [topic]` layered help (sessions/context/background/ai) with `/help` alias, `RunSetupFlowAsync(onlyIfNeeded)` shared flow
