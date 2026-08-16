using ECAssistant.Config;
using ECAssistant.Engine;
using ECAssistant.Controller;
using ECAssistant.Services;
using ECAssistant.Testing;
using ECAssistant.UI;

namespace ECAssistant;

public class Program
{
    [System.STAThread]
    public static async Task<int> Main(string[] args)
    {
        // ── Test mode: run automated tests ──
        if (args.Length > 0 && args[0].Equals("--test", StringComparison.OrdinalIgnoreCase))
        {
            return await RunTestsAsync(args.Skip(1).ToArray());
        }

        // ── Build config ──
        var userConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant");
        var logPath = Path.Combine(userConfigDir, "ECAssistant.log");
        var logger = new Logger(logPath, LogLevel.Info);

        var builder = AgentConfigBuilder.Create()
            .WorkingDirectory(userConfigDir);
        ApplyCommandLineArgsToBuilder(ref builder, args);

        var config = builder.Build();

        // ── Resolve model path ──
        var effectiveModelPath = config.Llm.ModelPath;
        if (!Path.IsPathRooted(effectiveModelPath))
        {
            var inWorkDir = Path.Combine(userConfigDir, effectiveModelPath);
            var inBuildDir = Path.Combine(AppContext.BaseDirectory, effectiveModelPath);
            if (File.Exists(inWorkDir))
                effectiveModelPath = inWorkDir;
            else if (File.Exists(inBuildDir))
                effectiveModelPath = inBuildDir;
            else
                effectiveModelPath = inWorkDir;
        }

        if (!File.Exists(effectiveModelPath))
        {
            Console.WriteLine($"[Error] Model not found: {effectiveModelPath}");
            Console.WriteLine($"[Hint] Put your .gguf model in: {userConfigDir} or set full path in appsettings.json");
            return 1;
        }

        // ── Create terminal and controller ──
        Directory.CreateDirectory(userConfigDir);
        Directory.CreateDirectory(Path.Combine(userConfigDir, config.Memory.DataPath));
        Directory.CreateDirectory(Path.Combine(userConfigDir, config.Workspace.Path));

        var console = new EGuiConsole();
        var controller = new AppController(
            console,
            config,
            effectiveModelPath,
            userConfigDir,
            userConfigDir,
            logger);

        return await controller.RunAsync();
    }

    // ── Command-line args ──

    private static void ApplyCommandLineArgsToBuilder(ref AgentConfigBuilder builder, string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].ToLower().TrimStart('-');
            switch (arg)
            {
                case "model":
                    if (i + 1 < args.Length) builder.WithModel(args[++i]); break;
                case "ctx":
                case "contextsize":
                    if (i + 1 < args.Length && uint.TryParse(args[++i], out uint ctx)) builder.ContextSize(ctx); break;
                case "gpu":
                case "gpulayers":
                case "gpu_layers":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int layers)) builder.GpuLayers(Math.Clamp(layers, 0, 100)); break;
                case "threads":
                case "threadcount":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int thr)) builder.Threads(thr); break;
                case "temp":
                case "temperature":
                    if (i + 1 < args.Length && float.TryParse(args[++i], out float t)) builder.Temperature(Math.Clamp(t, 0.0f, 2.0f)); break;
            }
        }
    }

    // ── Test Mode (unchanged from v10.25) ──

    private static async Task<int> RunTestsAsync(string[] testArgs)
    {
        bool useMock = testArgs.Any(a => a.Equals("--mock", StringComparison.OrdinalIgnoreCase));

        var userConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant");
        var config = AgentConfigBuilder.Create()
            .WorkingDirectory(userConfigDir)
            .Build();
        var modelPath = config.Llm.ModelPath;

        for (int i = 0; i < testArgs.Length; i++)
        {
            if (testArgs[i].Equals("--model", StringComparison.OrdinalIgnoreCase) && i + 1 < testArgs.Length)
                modelPath = testArgs[++i];
        }

        if (!useMock && (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath)))
        {
            Console.WriteLine($"❌ Model not found: {modelPath}");
            Console.WriteLine("   Set model path in ~/ECAssistant/appsettings.json or pass --model /path/to/model.gguf");
            Console.WriteLine("   Or use --mock for model-independent tests (no GGUF needed).");
            return 1;
        }

        string? filter = null;
        bool verbose = false;
        for (int i = 0; i < testArgs.Length; i++)
        {
            if (testArgs[i].Equals("--filter", StringComparison.OrdinalIgnoreCase) && i + 1 < testArgs.Length)
                filter = testArgs[++i];
            if (testArgs[i].Equals("--verbose", StringComparison.OrdinalIgnoreCase) || testArgs[i].Equals("-v", StringComparison.OrdinalIgnoreCase))
                verbose = true;
        }

        var allTests = useMock ? EcaTests.MockScenarios : EcaTests.All;
        List<TestScenario> tests;
        if (!string.IsNullOrEmpty(filter))
        {
            tests = allTests.Where(t => t.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (tests.Count == 0)
            {
                Console.WriteLine($"No tests match filter '{filter}'. Available:");
                foreach (var t in allTests)
                    Console.WriteLine($"  {t.Name}");
                return 1;
            }
        }
        else
        {
            tests = allTests;
        }

        Console.WriteLine($"\n🧪 Running {tests.Count} test(s) with {(useMock ? "MOCK ENGINE (no model)" : $"model: {Path.GetFileName(modelPath)}")}");
        Console.WriteLine($"   Filter: {filter ?? "(all)"}\n");

        await using var runner = new TestRunner(useMock ? "/mock/model.gguf" : modelPath) { Verbose = verbose, UseMockEngine = useMock };
        var results = await runner.RunAllAsync(tests);

        var logPath = Path.Combine(runner.TestRootDir, "test_results.log");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ECAssistant Test Results — {DateTime.UtcNow:O}");
        sb.AppendLine($"Model: {(useMock ? "MOCK ENGINE" : modelPath)}");
        sb.AppendLine();
        foreach (var r in results)
        {
            sb.AppendLine($"{(r.Passed ? "PASS" : "FAIL")} | {r.Name} | {r.Duration.TotalSeconds:F1}s | {r.FailureReason}");
            if (!r.Passed)
            {
                sb.AppendLine($"  Output: {r.FinalOutput}");
                sb.AppendLine();
            }
        }
        File.WriteAllText(logPath, sb.ToString());
        Console.WriteLine($"\n📝 Detailed log: {logPath}");

        return results.Any(r => !r.Passed) ? 1 : 0;
    }
}