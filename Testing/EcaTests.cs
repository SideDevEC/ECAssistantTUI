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
            Prompt = "Create a C# file called Program.cs with a simple Hello World console application. Include using System; a class Program; and a Main method that writes Hello World to the console. Make sure the file has actual content — do not create an empty file.",
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
                    // File must not be empty and must contain Hello World + class
                    return !string.IsNullOrWhiteSpace(content)
                        && content.Contains("Hello World", StringComparison.OrdinalIgnoreCase)
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
            // Accept 10 or 11 (trailing newline may add a line)
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var output = result.FinalOutput;
                    // Accept 10 or 11 (wc -l counts newlines, echo may add trailing newline)
                    return output.Contains("10") || output.Contains("11");
                }
            },
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

        // ── Tier 8: Parallel Multi-Tool Execution (v10.13) ───

        new TestScenario
        {
            Name = "parallel_create_three_files",
            Description = "Ask the LLM to create 3 files using SEPARATE toolcalls in one response — tests ParallelToolExecutor",
            Prompt = "Create three files simultaneously: alpha.txt with content 'A', beta.txt with content 'B', and gamma.txt with content 'G'. Use three separate EShellAgent toolcalls in a single response — do NOT chain them with semicolons. Each file must be its own toolcall.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "alpha.txt", "beta.txt", "gamma.txt" },
            MinToolCalls = 1, // 1 turn, but should be 3 toolcalls in that turn
            Assertions = new()
            {
                (result, ctx) =>
                {
                    // All 3 files must exist with correct content
                    var checks = new[] { ("alpha.txt", "A"), ("beta.txt", "B"), ("gamma.txt", "G") };
                    foreach (var (file, content) in checks)
                    {
                        var path = Path.Combine(ctx.WorkingDir, file);
                        if (!File.Exists(path)) return false;
                        var actual = File.ReadAllText(path).Trim();
                        if (!actual.Contains(content)) return false;
                    }
                    return true;
                }
            },
        },

        new TestScenario
        {
            Name = "parallel_mixed_tools",
            Description = "Use different tools in parallel — EShellAgent + EFileResearchTool in one response",
            Prompt = "Do two things at once in a single response with two separate toolcalls: (1) Use EShellAgent to create a file called marker.txt with content 'done', and (2) Use EFileResearchTool to scan the current directory for files. Use two separate toolcall blocks in one response.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "marker.txt" },
            MinToolCalls = 1,
        },

        new TestScenario
        {
            Name = "parallel_five_files",
            Description = "Create 5 files in parallel — stress test ParallelToolExecutor with 5 concurrent toolcalls",
            Prompt = "Create 5 files at the same time using 5 separate EShellAgent toolcalls in one response. Do NOT use semicolons. Each toolcall creates one file:\n1. p1.txt with content 'one'\n2. p2.txt with content 'two'\n3. p3.txt with content 'three'\n4. p4.txt with content 'four'\n5. p5.txt with content 'five'\nEach file must be its own <toolcall> block.",
            TimeoutSeconds = 240,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "p1.txt", "p2.txt", "p3.txt", "p4.txt", "p5.txt" },
            MinToolCalls = 1,
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var checks = new[] { ("p1.txt", "one"), ("p2.txt", "two"), ("p3.txt", "three"), ("p4.txt", "four"), ("p5.txt", "five") };
                    foreach (var (file, content) in checks)
                    {
                        var path = Path.Combine(ctx.WorkingDir, file);
                        if (!File.Exists(path)) return false;
                        var actual = File.ReadAllText(path).Trim();
                        if (!actual.Contains(content)) return false;
                    }
                    return true;
                }
            },
        },

        new TestScenario
        {
            Name = "parallel_create_and_git",
            Description = "Parallel: create a file via EShellAgent and create another via ECodeEditor in one response",
            Prompt = "Do two things at once with two separate toolcalls in one response: (1) Use EShellAgent to create a file called readme.md with content '# Test Project', and (2) Use ECodeEditor to create a file called notes.txt with content 'Parallel test notes'. Use two separate toolcall blocks.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "readme.md", "notes.txt" },
            MinToolCalls = 1,
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var readme = Path.Combine(ctx.WorkingDir, "readme.md");
                    var notes = Path.Combine(ctx.WorkingDir, "notes.txt");
                    if (!File.Exists(readme) || !File.Exists(notes)) return false;
                    var readmeContent = File.ReadAllText(readme);
                    var notesContent = File.ReadAllText(notes);
                    return readmeContent.Contains("# Test Project") && notesContent.Contains("Parallel test notes");
                }
            },
        },

        // ── Tier 9: Error Recovery & Resilience (v10.17.2) ────────

        new TestScenario
        {
            Name = "error_tool_failure_recovery",
            Description = "Force a tool failure then verify the agent self-corrects",
            Prompt = "Run the shell command 'cat nonexistent_file_xyz.txt' and then tell me what happened.",
            TimeoutSeconds = 120,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 1,
            // Should not crash — should report the error gracefully
            // Accept multiple phrasings: "not exist", "no such file", "doesn't exist", "not found"
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var output = result.FinalOutput.ToLowerInvariant();
                    return output.Contains("not exist") ||
                           output.Contains("no such file") ||
                           output.Contains("doesn't exist") ||
                           output.Contains("does not exist") ||
                           output.Contains("not found") ||
                           output.Contains("cannot find") ||
                           output.Contains("unable to find") ||
                           output.Contains("error");
                }
            },
        },

        new TestScenario
        {
            Name = "error_wrong_command_then_fix",
            Description = "Agent tries a wrong command, should self-correct and succeed",
            Prompt = "Create a file called fix_test.txt with content 'recovered'. If the first attempt fails, try a different approach.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "fix_test.txt" },
            MinToolCalls = 1,
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var path = Path.Combine(ctx.WorkingDir, "fix_test.txt");
                    if (!File.Exists(path)) return false;
                    return File.ReadAllText(path).Contains("recovered");
                }
            },
        },

        new TestScenario
        {
            Name = "error_invalid_tool_args",
            Description = "Agent uses wrong ECodeEditor args, should self-correct using create action",
            Prompt = "Use ECodeEditor to create a new file called new_code.txt with the content 'print(hello)'. If the first action fails, try a different action.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "new_code.txt" },
            MinToolCalls = 1,
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var path = Path.Combine(ctx.WorkingDir, "new_code.txt");
                    if (!File.Exists(path)) return false;
                    return File.ReadAllText(path).Contains("print(hello)");
                }
            },
        },

        new TestScenario
        {
            Name = "resilience_long_multi_step",
            Description = "Complex 5-step task requiring several turns — tests orchestrator persistence",
            Prompt = "Do the following steps in order: 1) Create a file called step1.txt with content 'done1'. 2) Create a file called step2.txt with content 'done2'. 3) Create a directory called step3dir. 4) Create a file inside step3dir called step3.txt with content 'done3'. 5) List all files and directories to verify everything was created.",
            TimeoutSeconds = 300,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "step1.txt", "step2.txt", "step3dir/step3.txt" },
            MinToolCalls = 1,
        },

        // ── Tier 10: Sub-Agent Tests (v10.18) ───────────────────

        new TestScenario
        {
            Name = "subagent_single_task",
            Description = "Spawn a single sub-agent to do a focused task",
            Prompt = "Use ESubAgent to spawn a sub-agent that creates a file called sub_result.txt with the content 'sub-agent was here'. Then tell me the result.",
            TimeoutSeconds = 300,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 1,
            ExpectedOutputContains = new() { "sub-agent" },
        },

        new TestScenario
        {
            Name = "subagent_parallel_spawn",
            Description = "Spawn 2 sub-agents in parallel to do independent tasks",
            Prompt = "Do two things at once using two separate ESubAgent toolcalls in one response: (1) Spawn a sub-agent to create a file called sub_a.txt with content 'agent A', and (2) Spawn a sub-agent to create a file called sub_b.txt with content 'agent B'. Use two separate toolcall blocks.",
            TimeoutSeconds = 300,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 1,
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var a = Path.Combine(ctx.WorkingDir, "sub_a.txt");
                    var b = Path.Combine(ctx.WorkingDir, "sub_b.txt");
                    if (!File.Exists(a) || !File.Exists(b)) return false;
                    return File.ReadAllText(a).Contains("agent A") && File.ReadAllText(b).Contains("agent B");
                }
            },
        },

        new TestScenario
        {
            Name = "subagent_error_recovery",
            Description = "Sub-agent handles a failing command and recovers — tests structured error handling",
            Prompt = "Use ESubAgent to spawn a sub-agent that runs 'cat nonexistent_file.txt' and then reports what happened. The sub-agent should handle the error gracefully.",
            TimeoutSeconds = 300,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 1,
            // Should complete — sub-agent handles the error and returns a result
        },

        // ── Tier 11: <lm> Container Tag System (v10.15) ──────────

        new TestScenario
        {
            Name = "tag_lm_direct_answer",
            Description = "Verify the model uses <lm> container with <output> for direct answers",
            Prompt = "What is the capital of France? Answer in one word.",
            TimeoutSeconds = 90,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedOutputContains = new() { "Paris" },
        },

        new TestScenario
        {
            Name = "tag_lm_toolcall_format",
            Description = "Verify the model uses <lm> container with <toolcall> for tool calls",
            Prompt = "Create a file called tag_test.txt with content 'tag system works'. Use EShellAgent to create it.",
            TimeoutSeconds = 120,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "tag_test.txt" },
            MinToolCalls = 1,
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var path = Path.Combine(ctx.WorkingDir, "tag_test.txt");
                    return File.Exists(path) &&
                           File.ReadAllText(path).Contains("tag system works");
                }
            },
        },

        new TestScenario
        {
            Name = "tag_lm_thinking_then_output",
            Description = "Verify <thinking> block is present but not leaked to user output",
            Prompt = "Think briefly about what 10 times 10 is, then give me just the number.",
            TimeoutSeconds = 90,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedOutputContains = new() { "100" },
        },

        // ── Tier 12: StepMapper / Two-Phase Planning (v10.17) ─────

        new TestScenario
        {
            Name = "stepmapper_multi_step",
            Description = "Multi-step task that should trigger StepMapper decomposition + mapping",
            Prompt = "Do these steps in order: 1) Create a file plan_a.txt with content 'step a done'. 2) Create a file plan_b.txt with content 'step b done'. 3) List the files in the current directory.",
            TimeoutSeconds = 240,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "plan_a.txt", "plan_b.txt" },
            MinToolCalls = 1,
        },

        new TestScenario
        {
            Name = "stepmapper_batch_execution",
            Description = "Task that can be batched into a single shell command with semicolons",
            Prompt = "Create three files in one shell command using semicolons: batch1.txt with content '1', batch2.txt with content '2', batch3.txt with content '3'. Then verify all files were created.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "batch1.txt", "batch2.txt", "batch3.txt" },
            MinToolCalls = 1,
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var checks = new[] { ("batch1.txt", "1"), ("batch2.txt", "2"), ("batch3.txt", "3") };
                    foreach (var (file, content) in checks)
                    {
                        var path = Path.Combine(ctx.WorkingDir, file);
                        if (!File.Exists(path)) return false;
                        if (!File.ReadAllText(path).Trim().Contains(content)) return false;
                    }
                    return true;
                }
            },
        },

        // ── Tier 13: Self-Correction & Resilience (v10.17.2) ──────

        new TestScenario
        {
            Name = "selfcorrect_format_retry",
            Description = "Model produces invalid format — orchestrator should retry with format reminder",
            Prompt = "Tell me about the weather. Use the proper response format with <lm> tags.",
            TimeoutSeconds = 120,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            // Should eventually produce valid output after at most 2 format retries
        },

        new TestScenario
        {
            Name = "selfcorrect_failure_streak",
            Description = "Multiple consecutive tool failures should trigger stop after maxFailures",
            Prompt = "Run these commands one at a time: 'cat /nonexistent/file1.txt', then 'cat /nonexistent/file2.txt', then 'cat /nonexistent/file3.txt'. Report what happened with each.",
            TimeoutSeconds = 300,
            // Should complete — either the agent self-corrects or stops after maxFailures
            // Either GoalAchieved or TurnsExhausted is acceptable
        },

        // ── Tier 14: Cross-Platform Shell (v10.16) ───────────────

        new TestScenario
        {
            Name = "xplatform_shell_detection",
            Description = "Verify shell agent detects the correct OS and shell",
            Prompt = "Run 'echo $SHELL' and 'uname -s' in one command and tell me the results.",
            TimeoutSeconds = 120,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 1,
            // On macOS: should contain 'Darwin' and '/bin/'
            // On Windows: would contain 'PowerShell' or 'NT'
            Assertions = new()
            {
                (result, ctx) =>
                {
                    var output = result.FinalOutput.ToLowerInvariant();
                    return output.Contains("darwin") || output.Contains("/bin/") ||
                           output.Contains("powershell") || output.Contains("nt");
                }
            },
        },

        new TestScenario
        {
            Name = "xplatform_file_operations",
            Description = "Cross-platform file creation and reading",
            Prompt = "Create a file called xplatform.txt with content 'works on all platforms'. Then read it back and confirm the content.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "xplatform.txt" },
            MinToolCalls = 1,
            ExpectedOutputContains = new() { "works on all platforms" },
        },

        // ── Tier 15: EGitTool (v10.16+) ──────────────────────────

        new TestScenario
        {
            Name = "git_status_check",
            Description = "Run git status in the working directory",
            Prompt = "Use EGitTool to check the git status of the current directory. Tell me what it says.",
            TimeoutSeconds = 120,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 1,
            // Should not crash — should report git status or 'not a git repo'
        },

        // ── Tier 16: EDotnetBuildTool (v10.16+) ─────────────────

        new TestScenario
        {
            Name = "dotnet_build_check",
            Description = "Run dotnet build on a simple project — should succeed or report errors",
            Prompt = "Create a file called Test.csproj with this content: <Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>. Then use EDotnetBuild to build it.",
            TimeoutSeconds = 180,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedFiles = new() { "Test.csproj" },
            MinToolCalls = 1,
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

    /// <summary>Get only the parallel tests.</summary>
    public static List<TestScenario> ParallelTests
        => ByNamePrefix("parallel_");

    /// <summary>Get only the error recovery tests.</summary>
    public static List<TestScenario> ErrorRecoveryTests
        => ByNamePrefix("error_").Concat(ByNamePrefix("resilience_")).ToList();

    /// <summary>Get only the sub-agent tests.</summary>
    public static List<TestScenario> SubAgentTests
        => ByNamePrefix("subagent_");

    /// <summary>Get only the <lm> tag tests.</summary>
    public static List<TestScenario> TagTests
        => ByNamePrefix("tag_");

    /// <summary>Get only the StepMapper tests.</summary>
    public static List<TestScenario> StepMapperTests
        => ByNamePrefix("stepmapper_");

    /// <summary>Get only the self-correction tests.</summary>
    public static List<TestScenario> SelfCorrectionTests
        => ByNamePrefix("selfcorrect_");

    /// <summary>Get only the cross-platform tests.</summary>
    public static List<TestScenario> CrossPlatformTests
        => ByNamePrefix("xplatform_");

    /// <summary>Get only the git tests.</summary>
    public static List<TestScenario> GitTests
        => ByNamePrefix("git_");

    /// <summary>Get only the dotnet build tests.</summary>
    public static List<TestScenario> DotnetTests
        => ByNamePrefix("dotnet_");

    // ── v10.22: Mock Engine Tests (model-independent, deterministic) ──────────

    /// <summary>Get only the mock engine tests.</summary>
    public static List<TestScenario> MockTests => ByNamePrefix("mock_");

    /// <summary>All mock engine test scenarios (model-independent).</summary>
    public static List<TestScenario> MockScenarios => new()
    {
        // ── Mock: Direct answer ──
        new TestScenario
        {
            Name = "mock_direct_answer",
            Description = "Mock engine returns a direct answer via <output>",
            Prompt = "What is 2+2?",
            TimeoutSeconds = 30,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            ExpectedOutputContains = new() { "4" },
            MaxToolCalls = 0,
        },

        // ── Mock: Tool call then answer ──
        new TestScenario
        {
            Name = "mock_toolcall_then_answer",
            Description = "Mock engine calls a tool, then gives final answer",
            Prompt = "Create a test file.",
            TimeoutSeconds = 30,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 1,
            MaxToolCalls = 2,
        },

        // ── Mock: Format retry ──
        new TestScenario
        {
            Name = "mock_format_retry",
            Description = "Mock engine sends malformed response, then corrects on retry",
            Prompt = "Test format retry.",
            TimeoutSeconds = 30,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
        },

        // ── Mock: Multi-step plan ──
        new TestScenario
        {
            Name = "mock_multistep",
            Description = "Mock engine executes multi-step plan with multiple tool calls",
            Prompt = "Multi-step task.",
            TimeoutSeconds = 30,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 2,
            MaxToolCalls = 4,
        },

        // ── Mock: Sub-task advancement ──
        new TestScenario
        {
            Name = "mock_subtask_advancement",
            Description = "Mock engine tests post-hoc sub-task matching",
            Prompt = "Create 3 files.",
            TimeoutSeconds = 30,
            ExpectedStatus = OrchestratorStatus.GoalAchieved,
            MinToolCalls = 1,
            MaxToolCalls = 3,
        },
    };}
