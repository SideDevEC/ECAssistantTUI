# AGENTS.md — ECAssistantTUI (AI-consumable)

Compact orientation for AI agents working in this repo. Humans: read README.md.

## Identity
- **Package:** `ECAssistant.TUI` v15.0.0 · net8.0 · namespace `ECAssistant.TUI.*`
- **Purpose:** Reusable terminal-UI library for ECAssistant hosts — streaming chat, session tabs, tool-call rendering, setup wizards.
- **Position:** Sits between Core (logic) and any host app. Console is the reference host. A library, NOT a framework — no hidden coupling.

## Fast orientation
- 30 types / 14 edges — small repo. `API-INDEX.md` (~520 tokens) covers it; `docs/packages/ECAssistantTUI.API.md` for the package API.
- Blueprint: `ARCHITECTURE.md`.

## What lives here
| Area | What |
|---|---|
| Chat rendering | Token-by-token streaming incl. thinking/reasoning blocks |
| Session tabs | Multiple conversations, independent scrollback, dirty indicators |
| Tool visualization | Invocations, approval prompts (approve/always/never), results — inline, nothing behind spinners |
| Decision checkpoints | Numbered interactive choices (Enter = autonomous fallback) |
| Wizards | First-run model install + provider configuration flows |
| ANSI/VT layer | Raw ANSI handling, cross-platform (macOS/Linux/Windows) |

## Non-negotiables
- **All rendering marshaled to the UI thread** — async-safe by construction; never render from a worker thread.
- Raw ANSI/VT only: no mouse sequences, terminal-native scrolling.
- No coupling to Console specifics — hosts plug in around Core; this library renders.

## Build & test
```bash
export EcaUseProjectRefs=true
dotnet build ECAssistant.TUI.csproj
dotnet test Tests/ECAssistant.TUI.Tests.csproj
```
- ⚠ csproj keeps an explicit `Compile` whitelist — NEW FILES MUST BE ADDED or they silently don't compile.
- One xUnit fact per behavior; no duplicate `[Fact]` names (has bitten before).

## Versioning & release
- LOCKSTEP version with all ECAssistant packages (15.0.0). Depends on `ECAssistant.Core` 15.0.0 (package default / project ref via `EcaUseProjectRefs`).
- Publish: tag `tui-v15.0.0` → CI → GitHub Packages + nuget.org. NEVER tag without Emre's "ship it".
- Release checklist: ARCHITECTURE.md updated · LDC regenerated · README touched if user-facing · build all projects 0 errors · Emre approves.
