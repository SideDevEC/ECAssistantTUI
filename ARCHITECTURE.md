# ECAssistant TUI — Architecture

**Updated:** 2026-09-19 (v12.9.9 — Core 12.9.8 wizard rework: remote GitHub catalog, flat model list with local discovery + on-disk highlight, vision derived not asked; LLM server 14.9.3 with shutdown grace)
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
## First-Run Setup (v12.9 — shared Core orchestrator)

- `AppController.RunSetupFlowAsync` now delegates to **`FirstRunOrchestrator` (Core)** — the exact same detect→wizard flow the Console host uses; TUI keeps no setup logic of its own
- Wizard stages (Core `SetupWizard` via `TuiSetupUi`): LLM local/remote → embeddings; **server binary install happens inside the wizard** (`ServerInstallCoordinator` + `NuGetServerFetcher` from Core) exactly when local chat OR local embeddings is chosen — never for pure-remote users
- `/reinstall` unchanged in UX: y/n confirm → stop server (verified) → config/keys reset (models kept) → shared wizard re-run → `ReloadAfterSetupAsync()` hot rebuild, no app restart
- Interactive version/foreign-install prompts (VERSION stamp mismatch, foreign `~/.ECAssistantLLM` layout) are rendered through `TuiSetupUi`
- All failures non-fatal — setup never blocks startup

## Changelog — 2026-09-18 (v12.9 setup refactor)

- Setup orchestration moved to Core (`FirstRunOrchestrator`); `AppController.RunSetupFlowAsync` is a thin delegation
- Removed TUI-side `EnsureServerBinaryInstalled` — server install is wizard-time (`ServerInstallCoordinator`), conditional on local chat/local embeddings, fetched from nuget.org (no embedded DLLs)

## Changelog — 2026-09-02 (post-reinstall hot reload)

- **AppController.ReloadAfterSetupAsync()** — after `/reinstall` + wizard, the runtime rebuilds in place: dispose renderers/sessions/SessionManager → reload `EAgentConfig` from appsettings.json via `ConfigLoader` → re-resolve model path (`ResolveModelPath`, mirrors EcaCompositionRoot rules) → rebuild sessions via shared `WireSessionAsync()` → switch to active session layer. No app restart needed.
- **WireSessionAsync(session)** — session→layer/renderer/builder wiring extracted; shared by `InitializeAppAsync` and reload path (no duplicated logic).
- `_config`/`_modelPath` fields now mutable (reloaded after setup).
- Regenerated API-INDEX via LDC generator.

## Changelog — 2026-08-27 (Installer Wizard + /menu)

- **FirstRunWizard**: local/remote AI choice; remote = endpoint/key/model/embedding-model + live connection test, key encrypted via SecureKeyStore.SetKey (keyfile: ref); local = catalog + internet/disk pre-flight, retry ×3, GPU preference → gpu_layers, memory estimates, embeddings ensure, post-install test, model removal
- **AppController**: `/reinstall` (warn → stop server, delete keys/config, models kept → wizard), `/menu [topic]` layered help (sessions/context/background/ai) with `/help` alias, `RunSetupFlowAsync(onlyIfNeeded)` shared flow

## Changelog — 2026-08-27 (evening: vision/embeddings wizard)

- Wizard question order: local/remote → vector memory → **embeddings source (local/remote, independent)** → vision → GPU → filtered catalog. Vision ON lists vision models only (mmproj wired automatically); OFF lists chat+embeddings. Orphans filtered by mmproj presence + vision choice.
- Remote setup asks embedding model id + vision capability; `/config` shows `Vision: enabled/disabled`.
- `/menu <topic>` layered help with topic back-stack (ESC walks back); `/help` alias.
- `/reinstall`: y/n warning → stop server, delete keys/config (models kept) → wizard.
- `EGuiConsole`: generic cross-thread prompt queue (`PromptViaInputLoop`) — installer prompts after `await` no longer fight the input loop.

## Changelog — 2026-08-30 (cleanup hardening)

- **ConsoleTerminalOutput**: `OnResize` event now actually raised (200ms poll) instead of CS0067 no-op
- **EGuiConsole**: silent-input mode no longer echoes typed characters; approval prompt wait is quit-aware (no deadlock when quit requested during cross-thread prompt)
- **LoadingIndicator**: label updates replace their line in the layer buffer — no raw `\r\x1b[2K` junk output in ANSI mode
- **AnsiInputParser**: pure streaming escape-sequence decoder (X10/SGR mouse, CSI/SS3 arrows, unknown-CSI drain)
- Case-preserving command parsing (session names case-insensitive-friendly), ANSI-safe line wrapping, crash guard on bare `/` input
- Root-only runtime contract: no dev base-dir model fallback

## Addendum — dependency chain rule (12.9.9)

**Rule (Emre):** Console → TUI → Core as a pure package chain. The Console csproj
references `ECAssistant.TUI` ONLY — Core arrives transitively (and remains directly
usable by hosts; nothing is hidden or wrapped). This guarantees the Console always
runs against the TUI's pinned Core version — no stale-Core escape hatch.

⚠ csproj note: the TUI project uses an explicit `Compile Include` whitelist — new
files MUST be added there or they are silently not compiled (bit us once today).
