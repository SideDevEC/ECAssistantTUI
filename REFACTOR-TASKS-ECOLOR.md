# EColor Refactor Tasks

**Summary:** Refactor EColor from static class to instance class implementing IColorFormatter.

## Tasks

### Phase 1: Core Changes
- [x] Rewrite `EColor.cs` — instance class implementing IColorFormatter
- [x] Update `Interfaces/IColorFormatter.cs` — add Write/WriteLine/Tag/TagBold + color properties
- [x] Update `Services/ColorFormatter.cs` — add new methods/properties

### Phase 2: Caller Updates (7 files with EColor.* calls)
- [x] `Program.cs` (98 calls + using static) — added static `_color` field, replaced all calls
- [x] `Engine/SubAgentManager.cs` (9 calls + using static) — added `_color` via constructor injection
- [x] `Memory/EMemoryManager.cs` (9 calls + using static) — added `IColorFormatter? color` to constructor
- [x] `Analysis/EContextAnalyzer.cs` (6 calls + using static) — added `IColorFormatter color` to constructor
- [x] `Engine/EDecisionLoop.cs` (6 calls + using static) — added `IColorFormatter color` to constructor
- [x] `Engine/SecondaryModelLoader.cs` (3 calls) — added `IColorFormatter? color` to constructor + Load method
- [x] `Services/Logger.cs` (0 EColor refs) — no changes needed (uses own ANSI constants)

### Phase 3: `using static` Removal (12 files)
- [x] `UI/EGuiConsole.cs` — removed (no EColor symbols used)
- [x] `Tools/EExample/EFileAnalyzer.cs` — removed (no EColor symbols used)
- [x] `Analysis/EContextAnalyzer.cs` — removed (done in Phase 2)
- [x] `Memory/EMemoryManager.cs` — removed (done in Phase 2)
- [x] `Testing/TestRunner.cs` — removed (no EColor symbols used)
- [x] `Engine/EDecisionLoop.cs` — removed (done in Phase 2)
- [x] `Engine/SubAgentManager.cs` — removed (done in Phase 2)
- [x] `Engine/EAgentEngine.cs` — removed (no EColor symbols used)
- [x] `Orchestrator.cs` — removed (no EColor symbols used)
- [x] `Program.cs` — removed (done in Phase 2)
- [x] `Session/LoadingIndicator.cs` — removed, added `IColorFormatter color` to constructor
- [x] `Session/ConsoleUiRenderer.cs` — removed, added `IColorFormatter color` to constructor

### Phase 4: Pre-existing Build Fixes
- [x] Fixed `ILogger` ambiguity in `EAgentEngine.cs` (qualified as `Microsoft.Extensions.Logging.ILogger` for NullLogger, `ECAssistant.Interfaces.ILogger?` for field/params)
- [x] Fixed `ToolPolicy` ambiguity in `Orchestrator.cs` (removed `using ECAssistant.Interfaces;`, qualified `ILogger`)
- [x] Fixed `ToolPolicy` ambiguity in `Session/AgentSession.cs` (qualified as `ECAssistant.Tools.ToolPolicy`)

### Phase 5: Build Verification
- [x] `dotnet build --nologo` — **0 errors, 0 warnings** ✅

**Status:** Complete
**Added:** 2026-08-15
**Completed:** 2026-08-15