# ECAssistant App — Architecture

**Updated:** 2026-08-16 (v11.0)
**Build:** 0 errors, 0 warnings
**Tests:** 66/66 passing

## Overview

ECAssistant is a full-screen terminal UI (TUI) for a local, offline AI coding assistant. The app is a thin frontend that wires into `ECAssistant.Core.dll` (built from the separate `ECAssistantCore` repo).

The architecture follows a strict three-layer separation:

1. **Controller** — application logic, layer-switching commands, session/layer lifecycle
2. **EGuiConsole** — terminal rendering engine, input reading, delta rendering
3. **BaseLayer** — per-screen state: output buffers, scroll position, status bar

Nobody knows about things above them. Dependencies flow strictly top-down.

## Project Structure

```
ECAssistant.sln / ECAssistant.slnx
├── ECAssistant.csproj              ← Console exe (App)
│     OutputType=Exe, AssemblyName=e-assistant
│     References: ECAssistant.Core.dll (from ECAssistantCore repo)
│     No LLamaSharp packages in source — runtime deps only
│     No content files — system prompts + config embedded in Core DLL
│
└── Tests/ECAssistant.Tests.csproj  ← UI tests only
      ProjectReference → ECAssistant.csproj
      Reference → ECAssistant.Core.dll
      66 tests
```

## Files (v11.0)

```
Program.cs                          ← Minimal entry point — creates Controller, calls Run()
Controller/
└── AppController.cs                ← Application logic, layer-switching, session management
UI/
├── EGuiConsole.cs                  ← Terminal engine (render + input + delta)
├── BaseLayer.cs                    ← Abstract layer: output buffer, scroll, status, ANSI helpers
├── SessionLayer.cs                 ← One per session, owns output buffer, handles session commands
├── StartupLayer.cs                 ← Always-present home screen, boot log, app status
├── HelpLayer.cs                    ← Help overlay
├── ConfigLayer.cs                  ← Config inspection overlay
├── LoadingIndicator.cs             ← Animated loading dots
└── ConsoleUiRenderer.cs            ← Bridges Core IOutputListener → SessionLayer buffer
```

## Dependency Flow

```
Program.cs
  │
  │ creates Controller(config, modelPath, workingDir, logger, ...)
  │ calls controller.Run()
  │ does nothing else
  ▼
AppController
  ├── creates → EGuiConsole (owns it)
  ├── creates → Dictionary<string, BaseLayer> (owns all layers)
  ├── holds  → ActiveLayer reference
  ├── holds  → SessionManager (Core)
  ├── holds  → config, logger, bgMgr (injected from Program)
  │
  │ EGuiConsole calls back (callbacks only):
  │   controller.OnPrompt(string input)       ← user hit Enter
  │   controller.OnEscapePressed()            ← user hit ESC
  │
  │ Controller handles only layer-switching:
  │   /help → switch to HelpLayer
  │   /home → switch to StartupLayer
  │   /config → switch to ConfigLayer
  │   /quit → shutdown
  │   /session <n> → switch to SessionLayer
  │   /session-new <name> → create + switch
  │   Everything else → activeLayer.ProcessInput(input)
  │   Layer returns false → controller handles SessionManager commands
  │
  ▼
EGuiConsole
  ├── owns: terminal (alt buffer, cursor, ANSI, dimensions)
  ├── owns: input buffer (accumulates keystrokes, renders input line)
  ├── owns: delta rendering (_screenRows cache, dirty flags)
  ├── owns: raw key reading (Console.ReadKey)
  ├── holds: ActiveLayer (set by controller via SetActiveLayer)
  ├── holds: Controller reference (for OnPrompt / OnEscapePressed callbacks)
  │
  │ Input loop:
  │   printable/backspace/tab → update input buffer → repaint input line
  │   Enter                   → controller.OnPrompt(input) → clear input buffer
  │   ESC                     → controller.OnEscapePressed()
  │   scroll keys / mouse     → activeLayer.HandleScroll(...)
  │   resize                  → activeLayer.UpdateDimensions(...)
  │
  ▼
BaseLayer (abstract)
  ├── owns: OutputLines[] buffer (with ANSI codes)
  ├── owns: ScrollOffset
  ├── owns: StatusBar string
  ├── owns: LiveStreamLine (for streaming token display)
  ├── owns: IsDirty flag
  ├── holds: EGuiConsole reference (set via BindToConsole, cleared via UnbindFromConsole)
  │
  ├── GetVisibleRows() → List<string>     ← rows to render, after scroll + wrap
  ├── GetStatusBar() → string             ← status bar content
  ├── GetInputPrompt() → string           ← "> " (default, override to change)
  ├── ProcessInput(string) → bool         ← /clear handled here, override for layer-specific
  ├── HandleScroll(int direction, int lines)
  ├── AddOutputLine(string text)          ← splits on \n, strips \r
  ├── UpdateLiveStreamLine(string text)   ← streaming token display
  ├── ClearLiveStreamLine()               ← remove live stream line
  ├── BindToConsole(EGuiConsole)          ← layer gets console reference for repaint
  ├── UnbindFromConsole()                 ← clear reference
  ├── RequestRepaint()                    ← calls console.RequestRepaint() if bound
  │
  ├── SessionLayer : BaseLayer
  │     ├── holds: Core AgentSession reference
  │     ├── holds: ConsoleUiRenderer (writes to THIS layer's buffer)
  │     ├── buffer fills continuously — even when not the active layer
  │     ├── ProcessInput: /clear-history, /save-context, /context-status,
  │     │   /stop, /tools, /single + forwards non-commands as prompts to Core
  │     └── defers /sessions, /session-peek, etc. to controller (returns false)
  │
  ├── StartupLayer : BaseLayer
  │     ├── always present, never deleted
  │     ├── shows boot log during startup, home screen after
  │     ├── displays: version, model, secondary model, config path, working dir, session count
  │     ├── ProcessInput: shows hint for non-commands, defers session commands to controller
  │     └── default active layer when no sessions exist
  │
  ├── HelpLayer : BaseLayer
  │     ├── static help content
  │     ├── ProcessInput: any input → return false (controller pops layer)
  │     └── ESC → controller pops layer
  │
  └── ConfigLayer : BaseLayer
        ├── read-only config inspection (LLM, agent, memory, workspace, tools, subagent)
        ├── BuildFromConfig() populates buffer from EAgentConfig
        ├── ProcessInput: any input → return false (controller pops layer)
        └── ESC → controller pops layer
```

## Screen Layout

```
┌─────────────────────────────────┐  row 0
│ Output region (from ActiveLayer) │  → layer.GetVisibleRows()
│ ...                              │  → EGuiConsole delta-renders against cache
│                                  │
├─────────────────────────────────┤  row (height-2)
│ Status bar                       │  → layer.GetStatusBar()
├─────────────────────────────────┤  row (height-1)
│ > user input here                │  → EGuiConsole input buffer (always visible)
└─────────────────────────────────┘
```

- Output region: rows 0 to (height-3)
- Status bar: row (height-2)
- Input line: row (height-1) — `> ` prompt visible on all layers
- EGuiConsole owns the input line (terminal concern)
- ActiveLayer owns the output region + status bar content

## Command Routing

```
User types input → Enter → Controller.OnPrompt(input)
  │
  ├── /help, /home, /config, /quit, /session, /session-new
  │     → Controller handles directly (layer switching / shutdown)
  │
  └── Everything else → activeLayer.ProcessInput(input)
        │
        ├── Returns true → handled by layer (e.g., /clear, /stop, /tools, prompt)
        │
        └── Returns false → Controller.HandleSessionManagerCommand(input)
              → /sessions, /session-peek, /session-stop, /session-close,
                /session-rename, /session-info, /session-queue, /tools
```

## ESC Handling

```
User presses ESC → Controller.OnEscapePressed()
  │
  ├── ActiveLayer is HelpLayer or ConfigLayer → pop back to prior layer
  ├── ActiveLayer is StartupLayer → no-op
  └── ActiveLayer is SessionLayer → stop running session
```

## Config

- Single config file: `~/ECAssistant/appsettings.json`
- No `eca-data/` subdirectory (removed in v11.0)
- Core's `AgentConfigBuilder` uses working directory directly
- Secondary model: `config.SecondaryModel.Enabled` and `.ModelPath`

## Key Constraints

- `Console.Write/WriteLine` ONLY in `EGuiConsole`
- `EColor` used ONLY by `ConsoleUiRenderer`, `LoadingIndicator`, layers, `Program.cs`
- App has zero LLamaSharp dependencies in source code (runtime deps only)
- App has zero external file dependencies (no appsettings.json in repo, no system prompts)
- Program.cs only interacts with AppController — nothing else
- EGuiConsole has no application logic — it renders and forwards input
- Layers have no knowledge of each other or the controller
- Controller is the only class that knows about all three (console + layers + Core)

## Build Order

```
1. Build ECAssistantCore.sln → produces ECAssistant.Core.dll
2. Copy DLL to ECAssistant/lib/
3. Build ECAssistant.sln → references the DLL via HintPath
```

## Test Summary (66 tests)

| File | Tests | What |
|---|---|---|
| `BaseLayerAnsiTests.cs` | 15 | StripAnsi, TruncateAnsi, WrapLine |
| `BaseLayerBufferTests.cs` | 10 | AddOutputLine, multi-line, \r\n, scroll reset, clear |
| `LayerTests.cs` | 21 | SessionLayer, HelpLayer, StartupLayer, ConfigLayer, scroll, live stream, ProcessInput |
| `ConsoleUiRendererTests.cs` | 16 | OnOutput tags, stream, RenderHistory, writes to SessionLayer |
| `EGuiConsoleRenderingTests.cs` | (planned) | Delta rendering with mock layer |
| `AppControllerTests.cs` | (planned) | OnPrompt command parsing, layer switching |