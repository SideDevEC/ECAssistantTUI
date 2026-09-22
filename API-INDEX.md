# API-INDEX.md — ECAssistantTUI

Generated: 2026-09-22T11:36:21.858044+00:00
Packages: 2  |  Types: 29

---

## ECAssistantTUI (22 types, ~3865 LOC)

- 🔵 IAppController  (ECAssistantTUI)
- 🔵 IGuiConsole  (ECAssistantTUI)
- 🔵 ITerminalOutput  (ECAssistantTUI)
- 🟡 AnsiInputParser  (ECAssistantTUI)
- 🟡 AnsiInputParserCursorPasteTests  (ECAssistantTUI)
- 🟡 AnsiInputParserTests  (ECAssistantTUI)
- 🟡 AppController : IAppController  (ECAssistantTUI)  deps: [IGuiConsole, EAgentConfig, string, string, string, ILogger, IGuiConsole, EAgentConfig, string, string, string, ILogger, List, IGuiConsole, EAgentConfig, string, string, string, ILogger, List, BackgroundProcessManager, FileWatcherService, IAiSetupResetter? setupResetter =]
- 🟡 BaseLayer  (ECAssistantTUI)
- 🟡 BaseLayerAnsiTests  (ECAssistantTUI)
- 🟡 BaseLayerBufferTests  (ECAssistantTUI)
- 🟡 ConfigLayer : BaseLayer  (ECAssistantTUI)  deps: [EColor]
- 🟡 ConsoleTerminalOutput : ITerminalOutput, IDisposable  (ECAssistantTUI)
- 🟡 ConsoleUiRenderer : IOutputListener, IDisposable  (ECAssistantTUI)  deps: [SessionLayer, EColor, Func, Func, IReadOnlyList]
- 🟡 ConsoleUiRendererTests  (ECAssistantTUI)
- 🟡 EGuiConsole : EGuiBase, IGuiConsole  (ECAssistantTUI)  deps: [ITerminalOutput]
- 🟡 EGuiConsoleTerminalRestoreTests  (ECAssistantTUI)
- 🟡 HelpLayer : BaseLayer  (ECAssistantTUI)  deps: [EColor, string]
- 🟡 LayerTests  (ECAssistantTUI)
- 🟡 LoadingIndicator : IDisposable  (ECAssistantTUI)  deps: [IGuiConsole, EColor]
- 🟡 SessionLayer : BaseLayer  (ECAssistantTUI)  deps: [string, string label =]
- 🟡 StartupLayer : BaseLayer  (ECAssistantTUI)  deps: [EColor]
- 🟡 TuiSetupUi : ISetupUi  (ECAssistantTUI)  deps: [IGuiConsole]

## Tests (7 types, ~852 LOC)

- 🟡 AnsiInputParserCursorPasteTests  (Tests)
- 🟡 AnsiInputParserTests  (Tests)
- 🟡 BaseLayerAnsiTests  (Tests)
- 🟡 BaseLayerBufferTests  (Tests)
- 🟡 ConsoleUiRendererTests  (Tests)
- 🟡 EGuiConsoleTerminalRestoreTests  (Tests)
- 🟡 LayerTests  (Tests)
