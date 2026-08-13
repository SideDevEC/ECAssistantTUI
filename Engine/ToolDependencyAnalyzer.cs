using System.Text.RegularExpressions;
using ECAssistant.Tools;
using ECAssistant.UI;

namespace ECAssistant.Engine;

/// <summary>
/// Analyzes a batch of toolcalls and determines which can run in parallel
/// vs which depend on results of earlier calls.
///
/// The analyzer uses heuristics based on tool type and argument content:
/// - Same tool targeting different files → independent
/// - Write/patch tool on file A + read tool on file A → read first (sequential)
/// - Build/git → always after any code-modifying tools
/// - Background exec → always independent
/// - Different tools, different targets → independent
///
/// No model hints needed — ECAssistant figures it out.
/// </summary>
public static class ToolDependencyAnalyzer
{
    // Tools that must run after any file modifications
    private static readonly HashSet<string> PostModifyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "EDotnetBuild", "EGitTool"
    };

    // Tools that are always independent (fire-and-forget) — forced into separate group
    private static readonly HashSet<string> AlwaysIndependentTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "EBackgroundExec"
    };

    /// <summary>
    /// Analyze a list of toolcalls and group them into dependency-ordered execution batches.
    /// Each batch contains toolcalls that can run in parallel.
    /// Batches must execute sequentially (batch N depends on batch N-1 results).
    /// </summary>
    public static List<DependencyGroup> Analyze(List<ToolCallRequest> toolCalls)
    {
        if (toolCalls.Count <= 1)
        {
            return new List<DependencyGroup>
            {
                new() { ToolCalls = toolCalls, GroupIndex = 0 }
            };
        }

        // Step 1: Extract file targets from each toolcall
        var targets = new List<HashSet<string>>();
        foreach (var tc in toolCalls)
        {
            targets.Add(ExtractTargets(tc));
        }

        // Step 2: Determine if each toolcall modifies files
        var modifies = new bool[toolCalls.Count];
        for (int i = 0; i < toolCalls.Count; i++)
        {
            modifies[i] = IsModifyingTool(toolCalls[i]);
        }

        // Step 3: Build dependency edges
        // tc[j] depends on tc[i] if:
        //   - tc[i] modifies a file that tc[j] reads or modifies (same target)
        //   - tc[j] is a post-modify tool (build/git) and tc[i] is a modifying tool
        //   - tc[i] and tc[j] target the same file and one of them writes
        var deps = new List<int>[toolCalls.Count];
        for (int i = 0; i < toolCalls.Count; i++)
            deps[i] = new List<int>();

        for (int i = 0; i < toolCalls.Count; i++)
        {
            for (int j = 0; j < toolCalls.Count; j++)
            {
                if (i == j) continue;

                // j depends on i?
                bool depends = false;

                // Rule 0: Always-independent tools never depend on anything
                if (AlwaysIndependentTools.Contains(toolCalls[j].ToolName ?? ""))
                {
                    depends = false;
                }
                else
                {
                    // Rule 1: i modifies a file that j also touches (read or write)
                    if (modifies[i] && targets[i].Overlaps(targets[j]))
                    {
                        depends = true;
                    }

                    // Rule 2: j is a post-modify tool (build/git) and i is a modifying tool
                    if (PostModifyTools.Contains(toolCalls[j].ToolName ?? "") && modifies[i])
                    {
                        depends = true;
                    }

                    // Rule 3: both are the same ECodeEditor targeting the same file
                    if (toolCalls[i].ToolName?.Equals("ECodeEditor", StringComparison.OrdinalIgnoreCase) == true &&
                        toolCalls[j].ToolName?.Equals("ECodeEditor", StringComparison.OrdinalIgnoreCase) == true &&
                        targets[i].Overlaps(targets[j]))
                    {
                        depends = true;
                    }
                }

                if (depends && !deps[j].Contains(i))
                    deps[j].Add(i);
            }
        }

        // Step 4: Topological sort into groups (Kahn's algorithm)
        // Each group = all nodes with no remaining dependencies → run in parallel
        var groups = new List<DependencyGroup>();
        var processed = new bool[toolCalls.Count];
        int groupIndex = 0;

        while (processed.Count(p => p) < toolCalls.Count)
        {
            var currentBatch = new List<ToolCallRequest>();

            // Find all unprocessed nodes with inDegree 0
            for (int i = 0; i < toolCalls.Count; i++)
            {
                if (!processed[i] && deps[i].All(d => processed[d]))
                {
                    currentBatch.Add(toolCalls[i]);
                    processed[i] = true;
                }
            }

            if (currentBatch.Count == 0)
            {
                // Circular dependency — just add remaining as a batch (shouldn't happen normally)
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    if (!processed[i])
                    {
                        currentBatch.Add(toolCalls[i]);
                        processed[i] = true;
                    }
                }
            }

            if (currentBatch.Count > 0)
            {
                groups.Add(new DependencyGroup { ToolCalls = currentBatch, GroupIndex = groupIndex++ });
            }
        }

        return groups;
    }

    /// <summary>
    /// Extract file/target references from a toolcall's arguments.
    /// Returns a set of normalized file paths (lowercase, trimmed).
    /// </summary>
    private static HashSet<string> ExtractTargets(ToolCallRequest tc)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tc.Args == null) return targets;

        foreach (var kvp in tc.Args)
        {
            if (string.IsNullOrEmpty(kvp.Value)) continue;

            var key = kvp.Key?.ToLowerInvariant() ?? "";
            var value = kvp.Value;

            // Args that contain file paths
            if (key == "file" || key == "path" || key == "filename" || key == "filepath")
            {
                foreach (var f in SplitPaths(value))
                    targets.Add(f);
            }

            // ECodeEditor: file= argument
            if (key == "file" && tc.ToolName?.Equals("ECodeEditor", StringComparison.OrdinalIgnoreCase) == true)
            {
                targets.Add(value.Trim().ToLowerInvariant());
            }

            // PowerShell: extract file paths from commands
            if (tc.ToolName?.Equals("EShellAgent", StringComparison.OrdinalIgnoreCase) == true &&
                (key == "command" || key == "script"))
            {
                targets.UnionWith(ExtractPathsFromPowerShell(value));
            }

            // EGitTool: file= argument
            if (tc.ToolName?.Equals("EGitTool", StringComparison.OrdinalIgnoreCase) == true && key == "file")
            {
                targets.Add(value.Trim().ToLowerInvariant());
            }
        }

        return targets;
    }

    /// <summary>Split a value that may contain multiple paths separated by ; or ,</summary>
    private static IEnumerable<string> SplitPaths(string value)
    {
        foreach (var part in value.Split(';', ',', '|'))
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                yield return trimmed.ToLowerInvariant();
        }
    }

    /// <summary>
    /// Extract file paths from PowerShell command text.
    /// Looks for Get-Content, Set-Content, Remove-Item, Copy-Item, etc. with file arguments.
    /// </summary>
    private static HashSet<string> ExtractPathsFromPowerShell(string command)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(command)) return paths;

        // Match patterns like: Get-Content "file.cs" or Set-Content path\to\file.txt
        var patterns = new[]
        {
            @"(?:Get-Content|Set-Content|Get-ChildItem|Remove-Item|Copy-Item|Move-Item|New-Item|Add-Content|Out-File|Tee-Object)\s+(?:-Path\s+)?[`'\""]?([^`'\"";|&\s]+)[`'\""]?",
            @"(?:Get-Content|Set-Content|Get-ChildItem|Remove-Item|Copy-Item|Move-Item|New-Item|Add-Content|Out-File|Tee-Object)\s+[`'\""]?([A-Za-z0-9_\\/.\-]+)[`'\""]?"
        };

        foreach (var pattern in patterns)
        {
            foreach (Match m in Regex.Matches(command, pattern, RegexOptions.IgnoreCase))
            {
                var p = m.Groups[1].Value.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(p) && p.Length > 2)
                    paths.Add(p);
            }
        }

        // Match .cs/.md/.json/.ps1/.txt file references anywhere
        foreach (Match m in Regex.Matches(command, @"\b([A-Za-z0-9_\-]+\.(?:cs|md|json|ps1|txt|csproj|sln|xaml))\b", RegexOptions.IgnoreCase))
        {
            paths.Add(m.Groups[1].Value.Trim().ToLowerInvariant());
        }

        return paths;
    }

    /// <summary>Check if a toolcall is likely to modify files (write/patch/delete).</summary>
    private static bool IsModifyingTool(ToolCallRequest tc)
    {
        var name = tc.ToolName ?? "";

        // ECodeEditor always modifies (patch/insert/delete/replace)
        if (name.Equals("ECodeEditor", StringComparison.OrdinalIgnoreCase))
            return true;

        // EShellAgent: check if command writes files
        if (name.Equals("EShellAgent", StringComparison.OrdinalIgnoreCase))
        {
            var cmd = tc.Args?.GetValueOrDefault("command") ?? "";
            return IsPowerShellWriteCommand(cmd);
        }

        // EGitTool: commit/push modify the repo
        if (name.Equals("EGitTool", StringComparison.OrdinalIgnoreCase))
        {
            var action = tc.Args?.GetValueOrDefault("action") ?? "";
            return action.Equals("commit", StringComparison.OrdinalIgnoreCase) ||
                   action.Equals("push", StringComparison.OrdinalIgnoreCase) ||
                   action.Equals("checkout", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Check if a PowerShell command writes/modifies files.</summary>
    private static bool IsPowerShellWriteCommand(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return false;
        var lower = cmd.ToLowerInvariant();

        string[] writePatterns = {
            "set-content", "add-content", "out-file", "tee-object",
            "remove-item", "copy-item", "move-item", "new-item",
            "invoke-webrequest", "start-process",
            "-replace", "mkdir", "rmdir", "del ", "rm ",
            "git commit", "git push", "git checkout", "git merge",
            "dotnet build", "dotnet test", "dotnet format", "dotnet run",
            "dotnet publish", "dotnet pack"
        };

        foreach (var p in writePatterns)
            if (lower.Contains(p)) return true;

        return false;
    }
}

/// <summary>
/// A group of toolcalls that can execute in parallel.
/// Groups are ordered — group N depends on results from groups 0..N-1.
/// </summary>
public class DependencyGroup
{
    public List<ToolCallRequest> ToolCalls { get; set; } = new();
    public int GroupIndex { get; set; }

    public bool IsParallel => ToolCalls.Count > 1;

    public override string ToString()
    {
        var names = string.Join(", ", ToolCalls.Select(tc => $"{tc.ToolName}#{tc.Index}"));
        return $"Group {GroupIndex} ({ToolCalls.Count} call{(ToolCalls.Count > 1 ? "s" : "")}): {names}";
    }
}

/// <summary>
/// A single parsed tool call request from the LLM response.
/// Contains tool name, arguments, and position index in the batch.
/// </summary>
public class ToolCallRequest
{
    public string? ToolName { get; set; }
    public Dictionary<string, string?> Args { get; set; } = new();
    public int Index { get; set; }  // 1-based position in the LLM response

    public override string ToString() => $"[{Index}] {ToolName}({string.Join(", ", Args.Select(kvp => $"{kvp.Key}={EGuiBase.Truncate(kvp.Value ?? "", 40)}"))})";
}