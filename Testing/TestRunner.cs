using System.Diagnostics;
using System.Text;
using ECAssistant.Config;
using ECAssistant.Engine;
using ECAssistant.Orchestration;
using ECAssistant.Tools;
using ECAssistant.Tools.Shell;
using ECAssistant.Tools.Research;
using ECAssistant.Tools.Background;
using ECAssistant.Tools.Web;
using ECAssistant.Tools.Build;
using ECAssistant.Tools.Git;
using ECAssistant.Tools.Code;
using ECAssistant.Tools.Reader;
using ECAssistant.Analysis;
using ECAssistant.Services;
using ECAssistant.Interfaces;
using ECAssistant.UI;
using LLama.Common;
using LLama.Sampling;

namespace ECAssistant.Testing;

/// <summary>
/// Test result for a single test scenario.
/// </summary>
public class TestResult
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string Description { get; set; } = "";
    public string FailureReason { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public string FinalOutput { get; set; } = "";
    public int ToolCallsMade { get; set; }
    public string CapturedLog { get; set; } = "";
}

/// <summary>
/// A single test scenario — a prompt + assertions about the result.
/// </summary>
public class TestScenario
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Prompt { get; set; } = "";

    /// <summary>Scripted inputs for approval prompts (e.g., "y" to approve).</summary>
    public string?[] ScriptedInputs { get; set; } = Array.Empty<string?>();

    /// <summary>Assertions to evaluate after the test runs.</summary>
    public List<Func<TestResult, TestContext, bool>> Assertions { get; set; } = new();

    /// <summary>Timeout in seconds (default 120 = 2 min).</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Files that should exist after the test (created by the agent).</summary>
    public List<string> ExpectedFiles { get; set; } = new();

    /// <summary>Files that should NOT exist after the test.</summary>
    public List<string> UnexpectedFiles { get; set; } = new();

    /// <summary>Strings that should appear in the final output.</summary>
    public List<string> ExpectedOutputContains { get; set; } = new();

    /// <summary>Strings that should NOT appear in the final output.</summary>
    public List<string> UnexpectedOutputContains { get; set; } = new();

    /// <summary>Expected orchestrator status.</summary>
    public OrchestratorStatus? ExpectedStatus { get; set; }

    /// <summary>Setup action to run before the test (e.g., create existing files).</summary>
    public Action<string>? Setup { get; set; }

    /// <summary>Minimum number of tool calls expected.</summary>
    public int MinToolCalls { get; set; } = 0;

    /// <summary>Maximum number of tool calls expected (0 = no limit).</summary>
    public int MaxToolCalls { get; set; } = 0;
}

/// <summary>
/// Context passed to assertion functions — gives access to the working dir, captured output, etc.
/// </summary>
public class TestContext
{
    public string WorkingDir { get; set; } = "";
    public EGuiTestHarness Gui { get; set; } = null!;
    public EAgentEngine Engine { get; set; } = null!;
    public AgentOrchestrator Orchestrator { get; set; } = null!;
    public OrchestratorResult Result { get; set; } = null!;
}

/// <summary>
/// Automated test runner for ECAssistant.
/// Sets up the engine, orchestrator, and tools in a sandboxed working directory,
/// runs test scenarios without console interaction, and reports results.
/// </summary>
public sealed class TestRunner : IAsyncDisposable
{
    private readonly string _modelPath;
    internal readonly string _testRootDir;
    private EAgentEngine? _engine;
    private AgentOrchestrator? _orchestrator;
    private EGuiTestHarness? _testGui;
    private BackgroundProcessManager? _bgMgr;
    private EShellAgent? _shellAgent;
    private readonly ILogger _logger;

    /// <summary>
    /// v10.23: Static GUI reference for test harness. Replaces the old Program.Gui coupling.
    /// Components that need the GUI during tests read from this instead of Program.Gui.
    /// </summary>
    public static EGuiTestHarness? TestGui { get; set; }

    public List<TestResult> Results { get; } = new();

    /// <summary>v10.17.2: Verbose mode — dump full captured log for each test (including token stream).</summary>
    public bool Verbose { get; set; } = false;

    /// <summary>v10.22: Use MockEngine instead of real LLM (model-independent tests).</summary>
    public bool UseMockEngine { get; set; } = false;

    /// <summary>Create a test runner with the given model path.</summary>
    /// <param name="modelPath">Absolute path to the GGUF model file.</param>
    /// <param name="testRootDir">Root directory for test sandboxes (default: ~/ECAssistant/tests/).</param>
    public TestRunner(string modelPath, string? testRootDir = null, ILogger? logger = null)
    {
        _modelPath = modelPath;
        _logger = logger ?? new Logger();
        // v10.19.2: All test artifacts stay inside the working directory (~/ECAssistant/tests/)
        _testRootDir = testRootDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "ECAssistant", "tests");
        Directory.CreateDirectory(_testRootDir);
    }

    /// <summary>Run a single test scenario and return the result.</summary>
    public async Task<TestResult> RunScenarioAsync(TestScenario scenario)
    {
        var result = new TestResult
        {
            Name = scenario.Name,
            Description = scenario.Description,
        };

        // Create a sandboxed working directory for this test
        var sandboxDir = Path.Combine(_testRootDir, scenario.Name.Replace(" ", "_"));
        if (Directory.Exists(sandboxDir))
            Directory.Delete(sandboxDir, true);
        Directory.CreateDirectory(sandboxDir);

        Console.WriteLine($"  ┌─ Test: {scenario.Name}");
        Console.WriteLine($"  │  Sandbox: {sandboxDir}");
        var sw = Stopwatch.StartNew();

        try
        {
            // Run setup action if provided (e.g., create files the test expects to exist)
            if (scenario.Setup != null)
            {
                try { scenario.Setup(sandboxDir); }
                catch (Exception ex) { Console.WriteLine($"  │  Setup error: {ex.Message}"); }
            }

            // Set up the non-interactive GUI harness
            _testGui = new EGuiTestHarness();
            // v10.23: Set Gui on TestRunner instead of Program.Gui (decoupled from App)
            TestGui = _testGui;

            // Queue scripted inputs (for approval prompts)
            _testGui.QueueInputs(scenario.ScriptedInputs);

            // Build the engine + tools (same as Program.Main but without the console loop)
            var (engine, orchestrator) = await SetupEngineAndToolsAsync(sandboxDir);
            _engine = engine;
            _orchestrator = orchestrator;

            // v10.22: If using mock engine, inject scripted responses based on test name
            if (UseMockEngine && engine is MockEngine mock)
            {
                InjectMockResponses(mock, scenario);
            }

            // Run the test with a timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(scenario.TimeoutSeconds));

            _engine.StartExecution();
            var orchResult = await orchestrator.ExecuteMultiStep(scenario.Prompt);
            _engine.EndExecution();

            result.FinalOutput = orchResult.FinalOutput ?? "";
            result.ToolCallsMade = orchResult.ToolCallsMade;
            result.CapturedLog = _testGui.CapturedOutput;

            // Run assertions
            var context = new TestContext
            {
                WorkingDir = sandboxDir,
                Gui = _testGui,
                Engine = _engine,
                Orchestrator = orchestrator,
                Result = orchResult,
            };

            bool allPassed = true;
            var failures = new List<string>();

            // Built-in assertions: expected files
            foreach (var file in scenario.ExpectedFiles)
            {
                var fullPath = Path.Combine(sandboxDir, file);
                if (!File.Exists(fullPath))
                {
                    allPassed = false;
                    failures.Add($"Expected file not found: {file}");
                }
            }

            // Built-in assertions: unexpected files
            foreach (var file in scenario.UnexpectedFiles)
            {
                var fullPath = Path.Combine(sandboxDir, file);
                if (File.Exists(fullPath))
                {
                    allPassed = false;
                    failures.Add($"Unexpected file found: {file}");
                }
            }

            // Built-in assertions: output contains
            foreach (var text in scenario.ExpectedOutputContains)
            {
                if (!result.FinalOutput.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    allPassed = false;
                    failures.Add($"Output missing expected text: \"{text}\"");
                }
            }

            // Built-in assertions: output does NOT contain
            foreach (var text in scenario.UnexpectedOutputContains)
            {
                if (result.FinalOutput.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    allPassed = false;
                    failures.Add($"Output contains unexpected text: \"{text}\"");
                }
            }

            // Built-in assertions: status
            if (scenario.ExpectedStatus.HasValue && orchResult.Status != scenario.ExpectedStatus.Value)
            {
                allPassed = false;
                failures.Add($"Expected status {scenario.ExpectedStatus.Value}, got {orchResult.Status}");
            }

            // Built-in assertions: tool call count
            if (scenario.MinToolCalls > 0 && orchResult.ToolCallsMade < scenario.MinToolCalls)
            {
                allPassed = false;
                failures.Add($"Expected at least {scenario.MinToolCalls} tool calls, got {orchResult.ToolCallsMade}");
            }
            if (scenario.MaxToolCalls > 0 && orchResult.ToolCallsMade > scenario.MaxToolCalls)
            {
                allPassed = false;
                failures.Add($"Expected at most {scenario.MaxToolCalls} tool calls, got {orchResult.ToolCallsMade}");
            }

            // Custom assertions
            foreach (var assertion in scenario.Assertions)
            {
                try
                {
                    if (!assertion(result, context))
                    {
                        allPassed = false;
                        failures.Add($"Custom assertion failed: {assertion.Method.Name}");
                    }
                }
                catch (Exception ex)
                {
                    allPassed = false;
                    failures.Add($"Assertion threw: {ex.Message}");
                }
            }

            result.Passed = allPassed;
            result.FailureReason = allPassed ? "" : string.Join("; ", failures);
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.FailureReason = $"Exception: {ex.Message}";
            if (ex.InnerException != null)
                result.FailureReason += $" | Inner: {ex.InnerException.Message}";
        }
        finally
        {
            // Clean up engine
            if (_engine != null)
            {
                try { await _engine.DisposeAsync(); } catch { }
                _engine = null;
            }
        }

        sw.Stop();
        result.Duration = sw.Elapsed;

        // Report
        var icon = result.Passed ? "✅" : "❌";
        Console.WriteLine($"  └─ {icon} {scenario.Name} ({sw.Elapsed.TotalSeconds:F1}s) — {(result.Passed ? "PASS" : "FAIL")}");
        if (!result.Passed)
        {
            Console.WriteLine($"     Reason: {result.FailureReason}");
            Console.WriteLine($"     Output: {TruncateForConsole(result.FinalOutput, 200)}");
        }

        // v10.17.2: Verbose mode — dump full captured log for debugging
        if (Verbose && _testGui != null)
        {
            var dumpPath = Path.Combine(_testRootDir, $"{scenario.Name.Replace(" ", "_")}_full_log.txt");
            _testGui.DumpToFile(dumpPath);
            Console.WriteLine($"     📝 Full log: {dumpPath}");
        }

        return result;
    }

    /// <summary>Run multiple test scenarios in sequence.</summary>
    public async Task<List<TestResult>> RunAllAsync(IEnumerable<TestScenario> scenarios)
    {
        Results.Clear();
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  ECAssistant Automated Test Suite");
        Console.WriteLine($"  Model: {Path.GetFileName(_modelPath)}");
        Console.WriteLine($"  Test Root: {_testRootDir}");
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine();

        foreach (var scenario in scenarios)
        {
            var result = await RunScenarioAsync(scenario);
            Results.Add(result);
            Console.WriteLine();
        }

        // Summary
        var passed = Results.Count(r => r.Passed);
        var failed = Results.Count(r => !r.Passed);
        var totalTime = Results.Sum(r => r.Duration.TotalSeconds);

        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine($"  Results: {passed} passed, {failed} failed, {Results.Count} total ({totalTime:F1}s)");
        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Failed tests:");
            foreach (var f in Results.Where(r => !r.Passed))
                Console.WriteLine($"    ❌ {f.Name}: {f.FailureReason}");
        }
        Console.WriteLine("═══════════════════════════════════════════");

        return Results;
    }

    /// <summary>Set up the engine, tools, and orchestrator for a test run.</summary>
    private async Task<(EAgentEngine engine, AgentOrchestrator orchestrator)> SetupEngineAndToolsAsync(string workingDir)
    {
        // Load config from the user's ~/ECAssistant/appsettings.json
        var userConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant");
        var configPath = Path.Combine(userConfigDir, "appsettings.json");
        var config = File.Exists(configPath)
            ? new Config.ConfigLoader(new Services.FileSystemAdapter()).Load(configPath)
            : new EAgentConfig();

        // Override model path with our test model
        config.Llm.ModelPath = _modelPath;
        config.Llm.GpuLayers = 15; // Match production config

        // Build inference params
        var inferenceParams = new InferenceParams
        {
            MaxTokens = config.Inference.MaxTokens,
            AntiPrompts = config.Inference.AntiPrompts.Length > 0
                ? config.Inference.AntiPrompts
                : new[] { "</s>" },
            OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = config.Sampling.Temperature,
                TopP = config.Sampling.TopP,
                TopK = config.Sampling.TopK,
                RepeatPenalty = config.Sampling.RepeatPenalty
            }
        };

        // Create the engine
        EAgentEngine engine;
        if (UseMockEngine)
        {
            // v10.22: Mock engine — no GGUF needed, returns predefined responses
            engine = new MockEngine(workingDir);
            engine.MockMode = true;
            engine.LoadContext();
            engine.WireSummaryService();
            engine.MockMode = false; // Reset for safety
        }
        else
        {
            engine = new EAgentEngine(
                modelPath: _modelPath,
                workingDir: workingDir,
                contextSize: config.Llm.ContextSize,
                gpuLayers: config.Llm.GpuLayers,
                threadCount: config.Llm.Threads,
                inferenceParams: inferenceParams,
                logger: _logger
            );

            engine.LoadContext();
            engine.WireSummaryService();
        }

        // Vector memory
        if (config.VectorMemory.Enabled)
        {
            var vecDir = Path.Combine(workingDir, config.VectorMemory.Directory);
            await engine.InitializeVectorMemoryAsync(vecDir);
        }

        // Self-correction
        engine.InitializeSelfCorrection(workingDir);

        // Project context
        await engine.InitializeProjectContextAsync(workingDir);

        // Task planner
        engine.InitializeTaskPlanner();

        // Secondary model (optional)
        if (config.SecondaryModel.Enabled && !string.IsNullOrEmpty(config.SecondaryModel.ModelPath))
        {
            var secPath = config.SecondaryModel.ModelPath;
            if (!Path.IsPathRooted(secPath))
            {
                var secInWork = Path.Combine(userConfigDir, secPath);
                var secInBuild = Path.Combine(AppContext.BaseDirectory, secPath);
                secPath = File.Exists(secInWork) ? secInWork : (File.Exists(secInBuild) ? secInBuild : secInWork);
            }
            var secondary = SecondaryModelLoader.Load(secPath,
                contextSize: config.SecondaryModel.ContextSize,
                gpuLayers: config.SecondaryModel.GpuLayers,
                temperature: config.SecondaryModel.Temperature,
                topP: config.SecondaryModel.TopP,
                topK: config.SecondaryModel.TopK,
                repeatPenalty: config.SecondaryModel.RepeatPenalty,
                maxTokens: config.SecondaryModel.MaxTokens,
                antiPrompts: config.SecondaryModel.AntiPrompts, logger: _logger);
            if (secondary != null)
                engine.SetSecondaryModel(secondary);
        }

        // Background process manager
        _bgMgr = new BackgroundProcessManager();

        // File watcher
        var fileWatcher = new FileWatcherService(workingDir, logger: _logger);
        fileWatcher.Start();

        // Register tools
        var processRunner = new ProcessRunner();
        var fileSystem = new FileSystemAdapter();
        var httpClient = new HttpClientAdapter();

        // v10.24: Pass EAgentConfig to tools instead of ConfigProvider
        _shellAgent = new EShellAgent(processRunner, config, workingDir);
        engine.RegisterTool(_shellAgent);
        engine.RegisterTool(new EBackgroundExecTool(_bgMgr, processRunner, fileSystem, config));
        engine.RegisterTool(new EWebSearchTool(httpClient, config));
        engine.RegisterTool(new EDotnetBuildTool(processRunner, config));
        engine.RegisterTool(new EGitTool(processRunner, fileSystem, config));
        engine.RegisterTool(new ECodeEditorTool(fileSystem, config));

        // v10.22: EFileReader + EWebFetch
        engine.RegisterTool(new EFileReaderTool(fileSystem, config));
        engine.RegisterTool(new EWebFetchTool(httpClient, config));

        // File research tool
        engine.RegisterTool(new EFileResearchTool(fileSystem, config));

        // Prefill KV cache (no-op for mock engine)
        if (!UseMockEngine)
            await engine.PrefillStaticPrefix();

        // Create orchestrator with tool policy (all allowed for tests)
        var policy = new ECAssistant.Tools.ToolPolicy();
        var orchestrator = new AgentOrchestrator(engine, sessionOutput: null, maxTurns: 10, maxFailures: 3, toolPolicy: policy, logger: _logger);

        // v10.18: Initialize sub-agent support (async — rebuilds KV cache)
        // v10.19.4: Only if enabled in config
        if (config.SubAgent.Enabled)
        {
            await orchestrator.InitializeSubAgentsAsync(workingDir);
        }

        return (engine, orchestrator);
    }

    private string TruncateForConsole(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        return text.Length <= max ? text : text.Substring(0, max) + " [...]";
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine != null)
        {
            try { await _engine.DisposeAsync(); } catch { }
        }
    }

    /// <summary>
    /// v10.22: Inject scripted mock responses based on the test scenario name.
    /// Each mock test gets deterministic responses that exercise specific orchestrator paths.
    /// </summary>
    private void InjectMockResponses(MockEngine mock, TestScenario scenario)
    {
        switch (scenario.Name)
        {
            case "mock_direct_answer":
                mock.AddResponse("<lm><thinking>2+2=4</thinking><output>4</output></lm>");
                break;

            case "mock_toolcall_then_answer":
                mock.AddResponse("<lm><thinking>Need to create a file</thinking><toolcall>EShellAgent<command>echo hello > test.txt</command></toolcall></lm>");
                mock.AddResponse("<lm><thinking>File created successfully</thinking><output>Done. Created test.txt.</output></lm>");
                break;

            case "mock_format_retry":
                mock.AddResponse("<lm><thinking>let me help</thinking><toolcall>EShellAgent<command>echo test</command></toolcall>");
                mock.AddResponse("<lm><thinking>trying again</thinking><output>Format retry worked.</output></lm>");
                break;

            case "mock_multistep":
                mock.AddResponse("<lm><thinking>step 1</thinking><toolcall>EShellAgent<command>echo step1 > step1.txt</command></toolcall></lm>");
                mock.AddResponse("<lm><thinking>step 2</thinking><toolcall>EShellAgent<command>echo step2 > step2.txt</command></toolcall></lm>");
                mock.AddResponse("<lm><thinking>all done</thinking><output>Completed 2 steps successfully.</output></lm>");
                break;

            case "mock_subtask_advancement":
                mock.AddResponse("<lm><thinking>create 3 files</thinking><toolcall>EShellAgent<command>echo f1 > f1.txt && echo f2 > f2.txt && echo f3 > f3.txt</command></toolcall></lm>");
                mock.AddResponse("<lm><thinking>all 3 files created</thinking><output>Created 3 files: f1.txt, f2.txt, f3.txt</output></lm>");
                break;

            default:
                mock.AddResponse("<lm><thinking>default mock</thinking><output>Mock response for: " + scenario.Name + "</output></lm>");
                break;
        }
    }
}
