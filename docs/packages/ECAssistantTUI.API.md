# ECAssistantTUI.API.md

Types: 18  |  LOC: 3335  |  ~1496 tokens

---

### Interface: IAppController
> Interface for the application controller that binds the TUI to Core.
Properties:
  - ECAssistant.Core.Memory.VectorMemoryStore? VectorMemory { get; set; }
  - ECAssistant.Core.Session.AgentSession? ActiveSession { get; set; }
Methods:
  - Task<int> RunAsync()
Cross-package deps: ECAssistant.Core.Memory, ECAssistant.Core.Session

### Interface: IGuiConsole
> Interface for the console terminal that AppController and layers depend on.
Properties:
  - bool IsQuitRequested { get; set; }
  - int ScreenWidth { get; set; }
  - int ScreenHeight { get; set; }
Methods:
  - void SetCallbacks(Action<string> onPrompt, Action onEscape)
  - void SetActiveLayer(BaseLayer? layer)
  - void SetSilentInputCheck(Func<bool>? check)
  - void SetSilentInputInitial(bool silent)
  - void InitConsole()
  - void ShutdownConsole()
  - void Quit()
  - void RequestRepaint()
  - void WriteLine(string text)
  - void WriteLineColored(string coloredText)
  - void WriteLineColored(string coloredText, int maxChars)
  - void WriteLine(string text, int maxChars)
  - void WriteRaw(string text)
  - void BlankLine()
  - string? PromptColored(string labelAndText)
  - string? PromptRaw(string label)
  - void InfoColored(string coloredText)
  - void WarningColored(string coloredText)
  - void WriteRawDirect(string text)
  - void ClearCanvas()
  - bool IsEscapePressed()
  - void LogInternal(string text)

### Interface: ITerminalOutput
> Abstraction for low-level terminal output operations.
Properties:
  - int WindowWidth { get; set; }
  - int WindowHeight { get; set; }
Methods:
  - void Write(string text)
  - void Flush()
  - void ClearScreen()
  - void SetCursorPosition(int row, int col)
  - void ShowCursor()
  - void HideCursor()
  - void EnableAlternateScreen()
  - void DisableAlternateScreen()
  - void EnableMouse()
  - void DisableMouse()

### Class: AppController
> Application controller — the binder between EGuiConsole, layers, and Core.
Implements: IAppController
Constructor:
  - AppController(IGuiConsole console, EAgentConfig config, string modelPath, string workingDir, string userConfigDir, ILogger logger, IGuiConsole console, EAgentConfig config, string modelPath, string workingDir, string userConfigDir, ILogger logger, List<EToolBase>? externalTools, IGuiConsole console, EAgentConfig config, string modelPath, string workingDir, string userConfigDir, ILogger logger, List<EToolBase>? externalTools, BackgroundProcessManager? backgroundProcesses, FileWatcherService? fileWatcher)
Cross-package deps: ECAssistant.Core, ECAssistant.Core.Config, ECAssistant.Core.Engine, ECAssistant.Core.Orchestration, ECAssistant.Core.Setup, ECAssistant.Core.Tools, ECAssistant.Core.Tools.Shell, ECAssistant.Core.Tools.Background, ECAssistant.TUI.UI, ECAssistant.Core.Session, ECAssistant.TUI.Session, ECAssistant.Core.Services, ECAssistant.Core.Analysis, ECAssistant.Core.Interfaces

### Class: BaseLayer
> Abstract base class for all layers in the EGuiConsole system.
Cross-package deps: ECAssistant.Core

### Class: BaseLayerAnsiTests
> Unit tests for BaseLayer static methods (ANSI helpers).
Cross-package deps: ECAssistant.Core, ECAssistant.TUI.UI

### Class: BaseLayerBufferTests
> Tests for BaseLayer output buffer management (AddOutputLine, _outputLines).
Cross-package deps: ECAssistant.Core, ECAssistant.TUI.UI

### Class: ConfigLayer
> Config layer — displays all EAgentConfig values in a readable format.
Implements: BaseLayer
Constructor:
  - ConfigLayer(EColor color)
Cross-package deps: ECAssistant.Core, ECAssistant.Core.Config

### Class: ConsoleTerminalOutput
> ITerminalOutput implementation using System.Console.
Implements: ITerminalOutput

### Class: ConsoleUiRenderer
> Bridges Core's IOutputListener to a SessionLayer's buffer.
Implements: IOutputListener, IDisposable
Constructor:
  - ConsoleUiRenderer(SessionLayer layer, EColor color, Func<string>? streamBufferGetter = null, Func<string, bool>? approvalPrompt = null)
Cross-package deps: ECAssistant.Core, ECAssistant.Core.Session, ECAssistant.TUI.UI

### Class: ConsoleUiRendererTests
> Tests for ConsoleUiRenderer — verifies it writes to SessionLayer's buffer
Cross-package deps: ECAssistant.Core, ECAssistant.Core.Session, ECAssistant.TUI.Session, ECAssistant.TUI.UI

### Class: EGuiConsole
> Pure terminal engine for ECAssistant.
Implements: EGuiBase, IGuiConsole
Constructor:
  - EGuiConsole(ITerminalOutput terminal)
Cross-package deps: ECAssistant.Core, ECAssistant.Core.UI

### Class: FirstRunWizard
> First-run model installer wizard for the TUI.
Constructor:
  - FirstRunWizard(IGuiConsole console, EColor color, ModelCatalogDocument catalog, ModelInstallerService installer, FirstRunStatus status, RemoteProviderSetupWriter? remoteWriter = null, VectorMemorySetupWriter? vectorMemoryWriter = null)
Cross-package deps: ECAssistant.Core, ECAssistant.Core.Config, ECAssistant.Core.Setup, ECAssistant.TUI.UI

### Class: HelpLayer
> Static help content layer. Shows the help screen with all available commands.
Implements: BaseLayer
Constructor:
  - HelpLayer(EColor color, string[] helpLines)
Cross-package deps: ECAssistant.Core

### Class: LayerTests
> Tests for BaseLayer subclasses (SessionLayer, HelpLayer).
Cross-package deps: ECAssistant.Core, ECAssistant.TUI.UI

### Class: LoadingIndicator
> Simple loading indicator — writes the label once, no animation.
Implements: IDisposable
Constructor:
  - LoadingIndicator(IGuiConsole gui, EColor color)
Cross-package deps: ECAssistant.Core, ECAssistant.TUI.UI

### Class: SessionLayer
> One instance per Core session. Owns the output buffer for that session.
Implements: BaseLayer
Constructor:
  - SessionLayer(string sessionKey, string label = "")
Cross-package deps: ECAssistant.Core, ECAssistant.Core.Engine, ECAssistant.Core.Session, ECAssistant.TUI.Session

### Class: StartupLayer
> The startup/home layer — always present, never deleted.
Implements: BaseLayer
Constructor:
  - StartupLayer(EColor color)
Cross-package deps: ECAssistant.Core
