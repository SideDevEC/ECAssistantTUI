# ECAssistant App — Summary

**Updated:** 2026-08-16 (v11.0)
**Build:** 0 errors, 0 warnings
**Tests:** 66/66 passing
**Repo:** https://github.com/LLamaDudeX/ECAssistant.git
**Core repo:** https://github.com/LLamaDudeX/ECAssistantCore.git

## What It Is

The console application frontend for ECAssistant — a local, offline AI coding assistant. Built in C# .NET 8. Provides a full-screen terminal UI (alternate buffer, like nano/vim) that wires into the ECAssistant.Core engine DLL.

## v11.0 Architecture Refactor (2026-08-16)

Major refactor separating concerns into three clean layers:

- **AppController** — application logic, command parsing (layer-switching only), session/layer lifecycle
- **EGuiConsole** — pure terminal engine (render + input + delta rendering), no app logic
- **BaseLayer** — per-screen state: output buffers, scroll position, status bar

### New Files
- `Controller/AppController.cs` — owns console + layer dictionary, handles /help /home /quit /session /session-new, delegates rest to layers
- `UI/BaseLayer.cs` — abstract layer with output buffer, scroll, status bar, ANSI helpers, ProcessInput
- `UI/StartupLayer.cs` — always-present home screen, boot log, app status, never deleted
- `UI/ConfigLayer.cs` — read-only config inspection (/config command)

### Removed Files
- `UI/IGuiLayer.cs` — replaced by BaseLayer

### Key Changes
- Each session gets its own SessionLayer with its own buffer — fills continuously even when not active
- ConsoleUiRenderer writes to SessionLayer buffer, not EGuiConsole
- Program.cs is minimal — creates controller, calls Run(), done
- Layer-level command parsing: BaseLayer handles /clear, SessionLayer handles session commands, controller handles only layer switching
- Input prompt `> ` visible on all layers (BaseLayer default)
- Startup layer is default when no sessions exist
- /home, /config, /help are overlay layers — ESC/Enter returns to prior layer

## Config Cleanup

- Removed `eca-data/` subdirectory — `~/ECAssistant/appsettings.json` is the single config file
- Fixed in both Core (AgentConfigBuilder) and App (Program.cs)
- Secondary model display fixed — uses config.SecondaryModel.Enabled/.ModelPath directly

## Project Structure

```
ECAssistant.sln
├── ECAssistant.csproj              ← Console exe (references Core.dll)
├── Controller/
│   └── AppController.cs             ← Application logic + layer lifecycle
├── Program.cs                       ← Minimal entry point
├── UI/
│   ├── EGuiConsole.cs               ← Terminal engine (render + input + delta)
│   ├── BaseLayer.cs                 ← Abstract layer: buffer, scroll, ANSI helpers
│   ├── SessionLayer.cs              ← One per session, owns output buffer
│   ├── StartupLayer.cs              ← Home screen, always present
│   ├── HelpLayer.cs                 ← Help overlay
│   ├── ConfigLayer.cs               ← Config inspection overlay
│   ├── LoadingIndicator.cs          ← Animated loading dots
│   └── ConsoleUiRenderer.cs         ← Bridges Core output → SessionLayer buffer
├── Tests/
│   ├── UI/                          ← BaseLayer, Layer, ConfigLayer tests
│   └── Session/                     ← ConsoleUiRenderer tests
├── ARCHITECTURE.md
├── SUMMARY.md
└── lib/ECAssistant.Core.dll         ← Built from ECAssistantCore repo
```

## Dependencies

- **ECAssistant.Core.dll** — the engine (from ECAssistantCore repo)
- **LLamaSharp** 0.27.0 — runtime dependency of Core
- **Standard .NET 8** — nothing else
- No external files (system prompts and config embedded in Core DLL)

## Build Order

1. Build `ECAssistantCore.sln` → produces `ECAssistant.Core.dll`
2. Copy DLL to `ECAssistant/lib/`
3. Build `ECAssistant.sln`

## Commands

| Command | Handled By | Description |
|---|---|---|
| /help | Controller | Show help overlay |
| /home | Controller | Go to startup/home layer |
| /config | Controller | Show config values |
| /quit, /exit | Controller | Stop all sessions and exit |
| /session \<n\> | Controller | Switch to session n |
| /session-new \<name\> | Controller | Create new session |
| /clear | BaseLayer | Clear output buffer |
| /clear-history | SessionLayer | Clear conversation history |
| /save-context | SessionLayer | Save transcript |
| /context-status | SessionLayer | Show context window usage |
| /stop | SessionLayer | Stop running session |
| /tools | SessionLayer | List registered tools |
| /single | SessionLayer | Reset to single-turn mode |
| /sessions | Controller (deferred) | List all sessions |
| /session-peek | Controller (deferred) | Peek at session output |
| /session-stop | Controller (deferred) | Stop session by index |
| /session-close | Controller (deferred) | Close session by index |
| /session-rename | Controller (deferred) | Rename session |
| /session-info | Controller (deferred) | Detailed session info |
| /session-queue | Controller (deferred) | Show prompt queue |
| ESC | Controller | Stop session (session layer) / return (overlay layers) |