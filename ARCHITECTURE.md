# ECAssistant TUI — Architecture

**Updated:** 2026-08-16 (v11.1)
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
│     No LLamaSharp packages in source — runtime deps only
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
└── AppController.cs                ← Application logic, layer-switching, session management
UI/
├── IGuiConsole.cs                  ← Interface for terminal injection (hosts implement this)
├── EGuiConsole.cs                  ← Terminal engine (implements IGuiConsole)
├── BaseLayer.cs                    ← Abstract layer: output buffer, scroll, status, ANSI helpers
├── SessionLayer.cs                 ← One per session, owns output buffer, handles session commands
├── StartupLayer.cs                 ← Always-present home screen, boot log, app status
├── HelpLayer.cs                    ← Help overlay
├── ConfigLayer.cs                  ← Config inspection overlay
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
- TUI has zero LLamaSharp dependencies in source code (runtime deps only)
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

## Test Summary (66 tests)

| File | Tests | What |
|---|---|---|
| `BaseLayerAnsiTests.cs` | 15 | StripAnsi, TruncateAnsi, WrapLine |
| `BaseLayerBufferTests.cs` | 10 | AddOutputLine, multi-line, scroll reset, clear |
| `LayerTests.cs` | 21 | SessionLayer, HelpLayer, StartupLayer, ConfigLayer, scroll, live stream |
| `ConsoleUiRendererTests.cs` | 16 | OnOutput tags, stream, RenderHistory, writes to SessionLayer |