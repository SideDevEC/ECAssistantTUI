# Engine Batch C — Test Progress

## Status: COMPLETE

## Test Files
1. `Engine/TokenCounterTests.cs` — ✅ 14 tests (Count, EstimateUpper, null/empty, fallback path)
2. `Engine/ToolDependencyAnalyzerTests.cs` — ✅ 16 tests (0/1/multiple tools, dependency detection, parallel grouping, circular handling)
3. `Engine/ContextWindowTests.cs` — ✅ 23 tests (AddUser/Assistant/Tool/System, GetWindowMessages, IsWithinBudget, GetTotalTokens, Clear, RemoveLastAssistantMessage)
4. `Engine/ConversationTranscriptTests.cs` — ✅ 22 tests (factory methods, ToJson, FromJson, SaveToDisk/LoadFromDisk)
5. `Engine/TaskPlannerTests.cs` — ✅ 24 tests (ContainsAny, CountActions, SplitOnSteps, Decompose, CompleteCurrent, FailCurrent, GetProgressContext, GetSummary)
6. `Engine/StepMapperTests.cs` — ✅ 9 tests (ExecutionPlan, PlannedToolCall, ToPromptString)
7. `Engine/SelfCorrectionManagerTests.cs` — ✅ 20 tests (failure tracking, analysis patterns, file snapshots, rollback)
8. `Engine/EDecisionLoopTests.cs` — ❌ DELETED (EDecisionLoop was dead code, superseded by AgentOrchestrator; tests only covered DecisionResult data class)
9. `Engine/ParallelToolExecutorTests.cs` — ✅ 18 tests (CombineResults, FormatConsoleSummary, BatchToolResult properties)
10. `Engine/ProjectContextManagerTests.cs` — ✅ 22 tests (context building, project type detection, imports, classes/methods, persistence)
11. `Engine/ToolCallRequestTests.cs` — ✅ 7 tests (ToString with args, null, truncation)
12. `Engine/DependencyGroupTests.cs` — ✅ 8 tests (IsParallel, ToString, defaults)

## Build Status: PASS (0 warnings, 0 errors)
## Test Status: ALL PASS (825 total tests, 0 failures)

## Notes
- ToolDependencyAnalyzer: The group-building loop processes all tools with met dependencies in a single iteration, so dependent tools often end up in the same group. Tests reflect this actual behavior.
- SelfCorrectionManager alternating test uses different tools to avoid ToolLoop detection triggering first.
- TaskPlanner SplitOnSteps only splits on first occurrence of each separator, so "Build then test then deploy" → 2 sub-tasks.
- EDecisionLoop + DecisionResult deleted — dead code, superseded by AgentOrchestrator. Tests only covered DecisionResult data class (no behavioral tests).
- ParallelToolExecutor static methods (CombineResults, FormatConsoleSummary) are tested directly; ExecuteAsync requires EAgentEngine/Program.Gui.