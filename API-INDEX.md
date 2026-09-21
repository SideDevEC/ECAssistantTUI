# API-INDEX.md — ECAssistantTUI

Generated: 2026-09-21T16:09:09.769565+00:00
Packages: 2  |  Types: 27

---

## ECAssistantTUI (21 types, ~3638 LOC)

- 🔵 IAppController  (ECAssistantTUI)
- 🔵 IGuiConsole  (ECAssistantTUI)
- 🔵 ITerminalOutput  (ECAssistantTUI)
- 🟡 AnsiInputParser  (ECAssistantTUI)
- 🟡 AnsiInputParserTests  (ECAssistantTUI)
- 🟡 AppController : IAppController  (ECAssistantTUI)  deps: [IGuiConsole, EAgentConfig, string, string, string, ILogger, IGuiConsole, EAgentConfig, string, string, string, ILogger, List, IGuiConsole, EAgentConfig, string, string, string, ILogger, List, BackgroundProcessManager, FileWatcherService, IAiSetupResetter? setupResetter =]
- 🟡 BaseLayer  (ECAssistantTUI)
- 🟡 BaseLayerAnsiTests  (ECAssistantTUI)
- 🟡 BaseLayerBufferTests  (ECAssistantTUI)
- 🟡 ConfigLayer : BaseLayer  (ECAssistantTUI)  deps: [EColor]
- 🟡 ConsoleTerminalOutput : ITerminalOutput, IDisposable  (ECAssistantTUI)
- 🟡 ConsoleUiRenderer : IOutputListener, IDisposable  (ECAssistantTUI)  deps: [SessionLayer, EColor, Func, Func]
- 🟡 ConsoleUiRendererTests  (ECAssistantTUI)
- 🟡 EGuiConsole : EGuiBase, IGuiConsole  (ECAssistantTUI)  deps: [ITerminalOutput]
- 🟡 EGuiConsoleTerminalRestoreTests  (ECAssistantTUI)
- 🟡 HelpLayer : BaseLayer  (ECAssistantTUI)  deps: [EColor, string]
- 🟡 LayerTests  (ECAssistantTUI)
- 🟡 LoadingIndicator : IDisposable  (ECAssistantTUI)  deps: [IGuiConsole, EColor]
- 🟡 SessionLayer : BaseLayer  (ECAssistantTUI)  deps: [string, string label =]
- 🟡 StartupLayer : BaseLayer  (ECAssistantTUI)  deps: [EColor]
- 🟡 TuiSetupUi : ISetupUi  (ECAssistantTUI)  deps: [IGuiConsole]

## Tests (6 types, ~743 LOC)

- 🟡 AnsiInputParserTests  (Tests)
- 🟡 BaseLayerAnsiTests  (Tests)
- 🟡 BaseLayerBufferTests  (Tests)
- 🟡 ConsoleUiRendererTests  (Tests)
- 🟡 EGuiConsoleTerminalRestoreTests  (Tests)
- 🟡 LayerTests  (Tests)
