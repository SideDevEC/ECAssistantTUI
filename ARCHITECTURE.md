# ECAssistant TUI — Architecture (as-is)

**Updated:** 2026-09-23 · **Build:** 0 errors · **Namespace:** `ECAssistant.TUI.*`
**History:** git log — this file describes the CURRENT state only.

## Overview

ECAssistantTUI is a full-screen terminal UI **class library** (not an executable) for ECAssistant. Any .NET 8 app can reference it — the standalone `ECAssistantConsole` launcher, or future GUI hosts like ECSQL's terminal pane.

Strict three-layer separation:

1. **Controller** — application logic, layer-switching commands, session/layer lifecycle
2. **GuiConsole** — terminal rendering engine, input reading, delta rendering
3. **BaseLayer** — per-screen state: output buffers, scroll position, status bar

Nobody knows about things above them. Dependencies flow strictly top-down.

## Project Structure

```
ECAssistantTUI/
├── ECAssistant.TUI.csproj       ← Class library; PackageReference ECAssistant.Core
│                                  (sibling ProjectReference when EcaUseProjectRefs=true)
│                                  InternalsVisibleTo: ECAssistant.TUI.Tests
└── Tests/
```

```
Controller/
└── AppController.cs             ← Application logic, layer switching, session lifecycle
UI/
├── IGuiConsole.cs               ← Terminal injection interface (hosts implement)
├── GuiConsole.cs                ← Terminal engine (implements IGuiConsole)
├── BaseLayer.cs                 ← Abstract layer: output buffer, scroll, status, ANSI helpers
├── SessionLayer.cs              ← Per-session layer; owns output buffer, session commands
├── StartupLayer.cs              ← Home screen, boot log, app status
├── HelpLayer.cs                 ← Help overlay
├── ConfigLayer.cs               ← Config inspection overlay
├── LoadingIndicator.cs          ← Animated loading dots
└── ConsoleUiRenderer.cs         ← Bridges Core IOutputListener → SessionLayer buffer
```

## IGuiConsole Interface

External apps host the TUI without System.Console:

- `GuiConsole` implements it for the standalone terminal
- ECSQL (or other hosts) implement it with a PTY-backed control
- `AppController` accepts `IGuiConsole` via constructor — fully injectable
- `RestoreTerminal()` is idempotent — routed from graceful shutdown AND `ProcessExit`/`CancelKeyPress` hooks, so init-failure, crash, and Ctrl-C exits never leak terminal state into the next launch
- Status callbacks (PromptChoice + ShowStatus) wired on BOTH renderer creation sites (new-session AND loaded-session paths)

## AppController

```csharp
new AppController(console, config, modelPath, workingDir, userConfigDir, logger,
                  externalTools /* nullable — host-injected custom tools */,
                  backgroundProcesses, fileWatcher, setupResetter);
```

Both construction paths call `SessionBuilder.BuildAsync(session, externalTools)` — external tools register first, then native tools. First-run setup goes through Core's `FirstRunOrchestrator` with a `TuiSetupUi` adapter.

## Dependency Flow

```
Host app → creates IGuiConsole + AppController → RunAsync()
AppController ─ owns layers dict, IGuiConsole, SessionManager (Core), external tools
GuiConsole    ─ terminal (alt buffer, cursor, ANSI, delta rendering) → callbacks OnPrompt/OnEscape
BaseLayer     ─ output buffer, scroll, status bar; concrete: Session/Startup/Help/Config layers
```

## Command Routing

```
input → Controller.OnPrompt(input)
  ├── /help, /home, /config, /quit, /session, /session-new → controller directly
  └── everything else → activeLayer.ProcessInput(input)
        ├── true  → handled by layer
        └── false → Controller.HandleSessionManagerCommand(input)
```

## Key Constraints

- `Console.Write/WriteLine` ONLY in `GuiConsole`
- `AnsiColor` used ONLY by layers and renderers (not in Core)
- Zero LLamaSharp, zero external file dependencies (system prompts + config live in Core)
- `AppController` interacts only via `IGuiConsole` — no direct `Console.*`
- `GuiConsole` has no application logic — renders and forwards input
- Layers know nothing about each other or the controller; the controller is the only class that knows all three

## Test Coverage

TUI test suite lives in `Tests/` (unit + UI tests); run filtered — never the full suite interactively (process hygiene rule).

## Release

`ECAssistant.TUI` ships via GitHub Packages + nuget.org (tag `tui-v*`). **Unified versioning:** all packages ship together under one shared version number; all PackageReferences bump in lockstep. See Core ARCHITECTURE.md "Release discipline".