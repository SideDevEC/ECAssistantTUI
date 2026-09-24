# API-INDEX.md — ECAssistantTUI

Generated: 2026-09-24T00:29:49.509486+00:00
Packages: 2  |  Types: 30

---

## ECAssistantTUI (23 types, ~4018 LOC)

- 🔵 IAppController  (ECAssistantTUI)
- 🔵 IGuiConsole  (ECAssistantTUI)
- 🔵 ITerminalOutput  (ECAssistantTUI)
- 🟡 AnsiInputParser  (ECAssistantTUI)
- 🟡 AnsiInputParserCursorPasteTests  (ECAssistantTUI)
- 🟡 AnsiInputParserTests  (ECAssistantTUI)
- 🟡 AppController : IAppController  (ECAssistantTUI)  deps: [IGuiConsole, AppConfig, string, string, string, ILogger, IGuiConsole, AppConfig, string, string, string, ILogger, List, IGuiConsole, AppConfig, string, string, string, ILogger, List, BackgroundProcessManager, FileWatcherService, IAiSetupResetter? setupResetter =]
- 🟡 BaseLayer  (ECAssistantTUI)
- 🟡 BaseLayerAnsiTests  (ECAssistantTUI)
- 🟡 BaseLayerBufferTests  (ECAssistantTUI)
- 🟡 ConfigLayer : BaseLayer  (ECAssistantTUI)  deps: [AnsiColor]
- 🟡 ConsoleTerminalOutput : ITerminalOutput, IDisposable  (ECAssistantTUI)
- 🟡 ConsoleUiRenderer : IOutputListener, IDisposable  (ECAssistantTUI)  deps: [SessionLayer, AnsiColor, Func, Func, ApprovalScope>? approvalPromptScoped =, IReadOnlyList, Action]
- 🟡 ConsoleUiRendererTests  (ECAssistantTUI)
- 🟡 GuiConsole : GuiBase, IGuiConsole  (ECAssistantTUI)  deps: [ITerminalOutput]
- 🟡 GuiConsoleTerminalRestoreTests  (ECAssistantTUI)
- 🟡 HelpLayer : BaseLayer  (ECAssistantTUI)  deps: [AnsiColor, string]
- 🟡 LayerTests  (ECAssistantTUI)
- 🟡 LoadingIndicator : IDisposable  (ECAssistantTUI)  deps: [IGuiConsole, AnsiColor]
- 🟡 SessionLayer : BaseLayer  (ECAssistantTUI)  deps: [string, string label =]
- 🟡 StartupLayer : BaseLayer  (ECAssistantTUI)  deps: [AnsiColor]
- 🟡 StatusIndicator : IDisposable  (ECAssistantTUI)  deps: [IGuiConsole, AnsiColor]
- 🟡 TuiSetupUi : ISetupUi  (ECAssistantTUI)  deps: [IGuiConsole]

## Tests (7 types, ~852 LOC)

- 🟡 AnsiInputParserCursorPasteTests  (Tests)
- 🟡 AnsiInputParserTests  (Tests)
- 🟡 BaseLayerAnsiTests  (Tests)
- 🟡 BaseLayerBufferTests  (Tests)
- 🟡 ConsoleUiRendererTests  (Tests)
- 🟡 GuiConsoleTerminalRestoreTests  (Tests)
- 🟡 LayerTests  (Tests)
