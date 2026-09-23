# RELATIONSHIP-GRAPH.md — ECAssistantTUI

Generated: 2026-09-22T14:28:54.739755+00:00
Edges: 14  |  Packages: 2

---

## ECAssistantTUI

- AppController ──implements──► IAppController (ECAssistantTUI)
- AppController ──uses──► IGuiConsole (ECAssistantTUI)
- AppController ──uses──► IGuiConsole (ECAssistantTUI)
- AppController ──uses──► IGuiConsole (ECAssistantTUI)
- ConfigLayer ──implements──► BaseLayer (ECAssistantTUI)
- ConsoleTerminalOutput ──implements──► ITerminalOutput (ECAssistantTUI)
- GuiConsole ──implements──► IGuiConsole (ECAssistantTUI)
- GuiConsole ──uses──► ITerminalOutput (ECAssistantTUI)
- HelpLayer ──implements──► BaseLayer (ECAssistantTUI)
- LoadingIndicator ──uses──► IGuiConsole (ECAssistantTUI)
- SessionLayer ──implements──► BaseLayer (ECAssistantTUI)
- StartupLayer ──implements──► BaseLayer (ECAssistantTUI)
- StatusIndicator ──uses──► IGuiConsole (ECAssistantTUI)
- TuiSetupUi ──uses──► IGuiConsole (ECAssistantTUI)

## Tests

- (no outgoing edges)
