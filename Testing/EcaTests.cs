using ECAssistant.Orchestration;

namespace ECAssistant.Testing;

/// <summary>
/// Predefined test scenarios for ECAssistant.
/// These exercise the full agent pipeline: LLM → orchestrator → tools → LLM → output.
/// Each test runs in its own sandboxed directory to avoid side effects.
/// </summary>
public static class EcaTests
{
    /// <summary>Get all predefined test scenarios.</summary>
    public static List<TestScenario> All => new()
    {
        // ── Tier 1: Smoke Tests ─────────────────────────────────

        new TestScenario
        {
            Name = "smoke_simple_answer",
            Description = "Ask a simple question that should get a direct <output> answer without tool calls",
            Prompt = "What is 2 + 2? Answer directly.",
            TimeoutSeconds = 90,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedOutputContains = new() { "4" },
            MinToolCalls = 0,
            MaxToolCalls = 1, // LLM might try a tool, but shouldn't need one
        },

        new TestScenario
        {
            Name = "smoke_what_day",
            Description = "Ask about the current day — should answer directly without tools",
            Prompt = "What day of the week is it today? Just answer with the day name.",
            TimeoutSeconds = 90,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
        },

        // ── Tier 2: Single Tool Call ───────────────────────────

        new TestScenario
        {
            Name = "tool_create_single_file",
            Description = "Ask the agent to create a single file with content",
            Prompt = "Create a file called hello.txt with the content 'Hello from ECAssistant!' in the current directory.",
            TimeoutSeconds = 120,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "hello.txt" },
            MinToolCalls = 1,
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var filePath = Path.Combine(ctx.WorkingDir, "hello.txt");
                    if (!File.Exists(filePath)) return false;
                    var content = File.ReadAllText(filePath);
                    return content.Contains("Hello from ECAssistant", StringComparison.OrdinalIgnoreCase);
                }
            },
        },

        new TestScenario
        {
            Name = "tool_list_files",
            Description = "Ask the agent to list files in the current directory using EShellAgent",
            Prompt = "List all files in the current directory using the shell.",
            TimeoutSeconds = 120,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 1,
        },

        new TestScenario
        {
            Name = "tool_echo_to_file",
            Description = "Ask the agent to write specific text to a file via shell",
            Prompt = "Write the text 'Test content 12345' to a file named test_output.txt using a shell command.",
            TimeoutSeconds = 120,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "test_output.txt" },
            MinToolCalls = 1,
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var filePath = Path.Combine(ctx.WorkingDir, "test_output.txt");
                    if (!File.Exists(filePath)) return false;
                    var content = File.ReadAllText(filePath);
                    return content.Contains("Test content 12345");
                }
            },
        },

        // ── Tier 3: Multi-Step Tasks ───────────────────────────

        new TestScenario
        {
            Name = "multi_create_three_files",
            Description = "Ask the agent to create 3 files — tests task decomposition + multi-step",
            Prompt = "Create three files: a.txt, b.txt, and c.txt. Each file should contain its own filename as content.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "a.txt", "b.txt", "c.txt" },
            MinToolCalls = 1, // Could be done in one batch with semicolons
            Assertions = new()
            {
                (result, ctx) =>
                {
                    foreach (var f in new[] { "a.txt", "b.txt", "c.txt" })
                    {
                        var path = Path.Combine(ctx.WorkingDir, f);
                        if (!File.Exists(path)) return false;
                    }
                    return true;
                }
            },
        },

        new TestScenario
        {
            Name = "multi_create_and_read",
            Description = "Create a file, then read it back — tests tool chaining",
            Prompt = "Create a file named data.txt containing 'ECAssistant Test Data'. Then read the file back and tell me what's in it.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "data.txt" },
            MinToolCalls = 1, // Create (maybe read in same call or separate)
            ExpectedOutputContains = new() { "ECAssistant Test Data" },
        },

        new TestScenario
        {
            Name = "multi_create_directory_and_file",
            Description = "Create a directory, then a file inside it — tests multi-step shell",
            Prompt = "Create a directory called 'testdir', then create a file inside it called 'info.txt' with the content 'Directory test successful'.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "testdir/info.txt" },
            MinToolCalls = 1,
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var path = Path.Combine(ctx.WorkingDir, "testdir", "info.txt");
                    if (!File.Exists(path)) return false;
                    var content = File.ReadAllText(path);
                    return content.Contains("Directory test successful");
                }
            },
        },

        // ── Tier 4: Code Editor Tool ───────────────────────────

        new TestScenario
        {
            Name = "code_create_csharp_file",
            Description = "Ask the agent to create a simple C# file",
            Prompt = "Create a C# file called Program.cs with a simple Hello World console application. Include using System; a class Program; and a Main method that writes Hello World to the console.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "Program.cs" },
            MinToolCalls = 1,
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var path = Path.Combine(ctx.WorkingDir, "Program.cs");
                    if (!File.Exists(path)) return false;
                    var content = File.ReadAllText(path);
                    return content.Contains("Hello World", StringComparison.OrdinalIgnoreCase)
                        && content.Contains("class", StringComparison.OrdinalIgnoreCase);
                }
            },
        },

        new TestScenario
        {
            Name = "code_edit_existing_file",
            Description = "Create a file, then ask the agent to modify it using ECodeEditor",
            Prompt = "I have a file called config.txt with the content 'version=1.0'. Use ECodeEditor to replace 'version=1.0' with 'version=2.0' in that file.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 1,
            Setup = (workingDir) =>
            {
                File.WriteAllText(Path.Combine(workingDir, "config.txt"), "version=1.0");
            },
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var path = Path.Combine(ctx.WorkingDir, "config.txt");
                    if (!File.Exists(path)) return false;
                    var content = File.ReadAllText(path);
                    return content.Contains("version=2.0") && !content.Contains("version=1.0");
                }
            },
        },

        // ── Tier 5: Complex Reasoning ──────────────────────────

        new TestScenario
        {
            Name = "complex_file_count",
            Description = "Create 5 files, then count them — tests multi-step + verification",
            Prompt = "Create 5 files named file1.txt through file5.txt, each containing a number from 1 to 5. You can chain all file creation commands with semicolons in a single shell command. After creating them, list the files to verify they exist, then tell me how many files you created.",
            TimeoutSeconds = 300,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "file1.txt", "file2.txt", "file3.txt", "file4.txt", "file5.txt" },
            MinToolCalls = 1,
            ExpectedOutputContains = new() { "5" },
        },

        new TestScenario
        {
            Name = "complex_read_and_summarize",
            Description = "Create a markdown file with content, then read it and summarize",
            Prompt = "Create a file called notes.md with the following content:\n# Project Notes\n\n## TODO\n- Fix the login bug\n- Add dark mode\n- Write tests\n\nThen read the file back and tell me what tasks are listed.",
            TimeoutSeconds = 240,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "notes.md" },
            MinToolCalls = 1,
            ExpectedOutputContains = new() { "login", "dark mode", "test" },
        },

        // ── Tier 6: Error Handling & Edge Cases ────────────────

        new TestScenario
        {
            Name = "edge_nonexistent_file",
            Description = "Ask the agent to read a file that doesn't exist — should handle gracefully",
            Prompt = "Read the file 'nonexistent_file.txt' and tell me what's in it.",
            TimeoutSeconds = 120,
            // Should not crash — should report file not found
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
        },

        new TestScenario
        {
            Name = "edge_empty_request",
            Description = "Send a very short, ambiguous request",
            Prompt = "hi",
            TimeoutSeconds = 90,
            // Should not crash — should respond somehow
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
        },

        // ── Tier 7: Shell-Specific Tests (macOS/zsh) ───────────

        new TestScenario
        {
            Name = "shell_mac_commands",
            Description = "Test macOS-specific shell commands (zsh)",
            Prompt = "Run 'echo $SHELL' and 'uname -s' and tell me what shell and OS this is running on.",
            TimeoutSeconds = 120,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 1,
            ExpectedOutputContains = new() { "Darwin" }, // uname -s returns Darwin on macOS
        },

        new TestScenario
        {
            Name = "shell_pipe_commands",
            Description = "Test piped shell commands",
            Prompt = "Create a file with 10 lines of text, then use a pipe command to count the lines. Tell me the count.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 1,
            ExpectedOutputContains = new() { "10" },
        },

        new TestScenario
        {
            Name = "shell_create_with_content",
            Description = "Create a file with multi-line content using shell heredoc or echo",
            Prompt = "Create a file called shopping.txt with the following 3 items, one per line:\nMilk\nBread\nEggs",
            TimeoutSeconds = 150,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "shopping.txt" },
            MinToolCalls = 1,
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var path = Path.Combine(ctx.WorkingDir, "shopping.txt");
                    if (!File.Exists(path)) return false;
                    var content = File.ReadAllText(path);
                    return content.Contains("Milk") && content.Contains("Bread") && content.Contains("Eggs");
                }
            },
        },
    };

    /// <summary>Get a subset of tests by name prefix.</summary>
    public static List<TestScenario> ByNamePrefix(string prefix)
        => All.Where(t => t.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Get only the smoke tests (quick, basic).</summary>
    public static List<TestScenario> SmokeTests
        => ByNamePrefix("smoke_");

    /// <summary>Get only the tool tests (single tool call).</summary>
    public static List<TestScenario> ToolTests
        => ByNamePrefix("tool_");

    /// <summary>Get only the multi-step tests.</summary>
    public static List<TestScenario> MultiStepTests
        => ByNamePrefix("multi_");

    /// <summary>Get only the code tests.</summary>
    public static List<TestScenario> CodeTests
        => ByNamePrefix("code_");

    /// <summary>Get only the complex tests.</summary>
    public static List<TestScenario> ComplexTests
        => ByNamePrefix("complex_");

    /// <summary>Get only the edge case tests.</summary>
    public static List<TestScenario> EdgeTests
        => ByNamePrefix("edge_");

    /// <summary>Get only the shell tests.</summary>
    public static List<TestScenario> ShellTests
        => ByNamePrefix("shell_");
}