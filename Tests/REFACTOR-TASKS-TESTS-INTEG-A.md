# Integration Tests Batch A — Orchestrator + Tool Pipeline

**Started:** 2026-08-15 09:55 GMT+2
**Completed:** 2026-08-15 10:15 GMT+2

## Tasks

### 1. OrchestratorIntegrationTests.cs
- [x] Write tests (8 tests)
- [x] Build passes
- [x] Tests pass

Tests written:
- SingleToolCall_ToolExecutesThenFinalAnswer_VerifiesToolWasCalled
- MultipleToolCallsInOneResponse_BothExecuted_VerifiesBothRan
- MultiTurn_ToolCallThenAnswer_VerifiesTwoGenerateCalls
- MaxTurnsReached_LLMAlwaysCallsTools_TurnsExhaustedStatus
- ToolFailure_ProcessRunnerReturnsError_ErrorHandledGracefully
- DirectAnswer_NoToolCalls_VerifiesNoToolExecution
- EmptyResponse_HandledAsInvalidFormat_RetriesThenExhausts
- NoTags_ResponseTriggersFormatRetry_ThenValidAnswer
- UnknownTool_NotRegistered_ErrorHandledInOrchestrator

### 2. ToolPipelineIntegrationTests.cs
- [x] Write tests (10 tests)
- [x] Build passes
- [x] Tests pass

Tests written:
- EShellAgent_ThroughOrchestrator_ProcessRunnerCalledWithCorrectCommand
- EFileReader_ThroughOrchestrator_ToolDispatchedAndResultReturned
- ECodeEditor_Create_ThroughOrchestrator_ToolDispatched
- ECodeEditor_Patch_ThroughOrchestrator_ToolDispatched
- ToolAdapter_WrappingITool_ThroughOrchestrator_IToolExecuteAsyncCalled
- ToolPolicy_BlockedTool_ToolNotExecuted
- ToolPolicy_ApprovalRequired_UserApproves_ToolExecutes
- ToolPolicy_ApprovalRequired_UserDenies_ToolNotExecuted
- EShellAgent_ThroughOrchestrator_CreatesRealFile_Verified

### 3. SubAgentIntegrationTests.cs
- [x] Write tests (10 tests)
- [x] Build passes
- [x] Tests pass

Tests written:
- InitializeSubAgents_WithMockEngine_ESubAgentToolRegistered
- InitializeSubAgents_WithMockEngine_SubAgentManagerNotNull
- InitializeSubAgents_WithMockEngine_PolicyAllowsESubAgent
- SubAgentToolCall_ThroughOrchestrator_ReturnsResult
- SubAgentToolCall_MissingTaskArgument_ReturnsError
- SubAgentManager_Defaults_AreSetFromConfig
- SubAgentManager_CancelAll_WhenNoActiveAgents_DoesNotThrow
- SubAgentManager_GetActiveStatus_WhenNoAgents_ReturnsEmptyList
- MultipleSubAgentCalls_BothDispatched_OrchestratorContinues

### 4. ParallelToolExecutorIntegrationTests.cs
- [x] Write tests (12 tests)
- [x] Build passes
- [x] Tests pass

Tests written:
- DependencyAnalyzer_TwoIndependentReadTools_ReturnsSingleGroup
- DependencyAnalyzer_TwoCodeEditorCalls_SameFile_CircularDependencyReturnsSingleGroup
- DependencyAnalyzer_DifferentFiles_ReturnsSingleParallelGroup
- DependencyAnalyzer_CodeEditorThenBuild_SameIterationSingleGroup
- DependencyAnalyzer_SingleCall_ReturnsSingleGroup
- DependencyAnalyzer_EmptyList_ReturnsSingleEmptyGroup
- DependencyAnalyzer_ThreeIndependentReads_SingleGroup
- DependencyAnalyzer_WriteThenRead_SameFile_SingleGroupWithOrdering
- TwoIndependentTools_DifferentFiles_BothExecuteInSameBatch
- TwoDependentTools_SameFileWriteThenRead_BothExecuteThroughOrchestrator
- ParallelExecution_BatchOutput_ContainsBothToolResults
- ParallelBatch_OneToolFails_OtherStillSucceeds

## Infrastructure Changes

### MockEngine fix (Testing/MockEngine.cs + Engine/AgentEngine.cs)
- **Problem:** MockEngine constructor called base() which tried to load `/mock/model.gguf` GGUF file
- **Fix:** Added `AgentEngine._sForceMockMode` static flag, set by `MockEngine.ActivateMockMode()` helper
  called as a side-effect in the `base()` argument list. The AgentEngine constructor checks this flag
  and skips all LLama native initialization when true.

### Test serialization (Tests/Integration/ProgramGuiCollection.cs)
- Added `[CollectionDefinition(DisableParallelization = true)]` to serialize tests that share `Program.Gui`
- All 4 integration test classes decorated with `[Collection("ProgramGuiCollection")]`

## Summary
- Total new tests: 39 (all passing)
- Full suite: 912 tests pass
- Build: 0 errors, 0 warnings (test project)