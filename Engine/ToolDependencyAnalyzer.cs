using System.Text.RegularExpressions;
using ECAssistant.Tools;

namespace ECAssistant.Engine;

/// <summary>
/// Analyzes a batch of toolcalls and determines which can run in parallel
/// vs which depend on results of earlier calls.
/// </summary>
public class ToolDependencyAnalyzer
{
    private readonly HashSet<string> _postModifyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "EDotnetBuild", "EGitTool"
    };

    private readonly HashSet<string> _alwaysIndependentTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "EBackgroundExec"
    };

    public List<DependencyGroup> Analyze(List<ToolCallRequest> toolCalls)
    {
        if (toolCalls.Count <= 1)
        {
            return new List<DependencyGroup>
            {
                new() { ToolCalls = toolCalls, GroupIndex = 0 }
            };
        }

        var targets = new List<HashSet<string>>();
        foreach (var tc in toolCalls)
        {
            targets.Add(ExtractTargets(tc));
        }

        var modifies = new bool[toolCalls.Count];
        for (int i = 0; i < toolCalls.Count; i++)
        {
            modifies[i] = IsModifyingTool(toolCalls[i]);
        }

        var deps = new List<int>[toolCalls.Count];
        for (int i = 0; i < toolCalls.Count; i++)
            deps[i] = new List<int>();

        for (int i = 0; i < toolCalls.Count; i++)
        {
            for (int j = 0; j < toolCalls.Count; j++)
            {
                if (i == j) continue;

                bool depends = false;

                if (_alwaysIndependentTools.Contains(toolCalls[j].ToolName ?? ""))
                {
                    depends = false;
                }
                else
                {
                    if (modifies[i] && targets[i].Overlaps(targets[j]))
                        depends = true;

                    if (_postModifyTools.Contains(toolCalls[j].ToolName ?? "") && modifies[i])
                        depends = true;

                    if (toolCalls[i].ToolName?.Equals("ECodeEditor", StringComparison.OrdinalIgnoreCase) == true &&
                        toolCalls[j].ToolName?.Equals("ECodeEditor", StringComparison.OrdinalIgnoreCase) == true &&
                        targets[i].Overlaps(targets[j]))
                        depends = true;
                }

                if (depends && !deps[j].Contains(i))
                    deps[j].Add(i);
            }
        }

        var groups = new List<DependencyGroup>();
        var processed = new bool[toolCalls.Count];
        int groupIndex = 0;

        while (processed.Count(p => p) < toolCalls.Count)
        {
            var currentBatch = new List<ToolCallRequest>();

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
                groups.Add(new DependencyGroup { ToolCalls = currentBatch, GroupIndex = groupIndex++ });
        }

        return groups;
    }

    private HashSet<string> ExtractTargets(ToolCallRequest tc)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tc.Args == null) return targets;

        foreach (var kvp in tc.Args)
        {
            if (string.IsNullOrEmpty(kvp.Value)) continue;

            var key = kvp.Key?.ToLowerInvariant() ?? "";
            var value = kvp.Value;

            if (key == "file" || key == "path" || key == "filename" || key == "filepath")
            {
                foreach (var f in SplitPaths(value))
                    targets.Add(f);
            }

            if (key == "file" && tc.ToolName?.Equals("ECodeEditor", StringComparison.OrdinalIgnoreCase) == true)
                targets.Add(value.Trim().ToLowerInvariant());

            if (tc.ToolName?.Equals("EShellAgent", StringComparison.OrdinalIgnoreCase) == true &&
                (key == "command" || key == "script"))
                targets.UnionWith(ExtractPathsFromPowerShell(value));

            if (tc.ToolName?.Equals("EGitTool", StringComparison.OrdinalIgnoreCase) == true && key == "file")
                targets.Add(value.Trim().ToLowerInvariant());
        }

        return targets;
    }

    private IEnumerable<string> SplitPaths(string value)
    {
        foreach (var part in value.Split(';', ',', '|'))
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                yield return trimmed.ToLowerInvariant();
        }
    }

    private HashSet<string> ExtractPathsFromPowerShell(string command)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(command)) return paths;

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

        foreach (Match m in Regex.Matches(command, @"\b([A-Za-z0-9_\-]+\.(?:cs|md|json|ps1|txt|csproj|sln|xaml))\b", RegexOptions.IgnoreCase))
            paths.Add(m.Groups[1].Value.Trim().ToLowerInvariant());

        return paths;
    }

    private bool IsModifyingTool(ToolCallRequest tc)
    {
        var name = tc.ToolName ?? "";

        if (name.Equals("ECodeEditor", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Equals("EShellAgent", StringComparison.OrdinalIgnoreCase))
        {
            var cmd = tc.Args?.GetValueOrDefault("command") ?? "";
            return IsPowerShellWriteCommand(cmd);
        }

        if (name.Equals("EGitTool", StringComparison.OrdinalIgnoreCase))
        {
            var action = tc.Args?.GetValueOrDefault("action") ?? "";
            return action.Equals("commit", StringComparison.OrdinalIgnoreCase) ||
                   action.Equals("push", StringComparison.OrdinalIgnoreCase) ||
                   action.Equals("checkout", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private bool IsPowerShellWriteCommand(string cmd)
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