using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ECAssistant.Config;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Code;

/// <summary>
/// Code Editor Tool — surgical code edits with diff preview, multi-line replacement,
/// cross-file search & replace, and syntax-aware editing.
/// </summary>
public class ECodeEditorTool : EToolBase
{
    private readonly IFileSystem _fileSystem;
    private readonly JsonElement? _toolConfig;
    private readonly string _workingDir;

    public override string Name => "ECodeEditor";

    public override string Description =>
        "Surgical code editing: create files, multi-line patch, diff preview, cross-file search & replace, " +
        "line insertion/deletion. Better than shell echo for code changes.";

    public override string UsageExample =>
        "<toolcall>ECodeEditor<action>patch</action><file>Program.cs</file><old_text>bug</old_text><new_text>fix</new_text></toolcall>";

    public override bool IsEnabled { get; protected set; } = true;

    public ECodeEditorTool(IFileSystem fileSystem, EAgentConfig config)
    {
        _fileSystem = fileSystem;
        config.Tools.TryGetValue(Name, out var tc);
        _toolConfig = tc.ValueKind == JsonValueKind.Undefined ? null : tc;
        IsEnabled = ReadCfg(_toolConfig, "enabled", true);
        _workingDir = config.AgentSettings.WorkingDirectory;
    }

    public override object GetConfigSection() => new { enabled = true };

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var action = arguments.GetValueOrDefault("action")?.ToLower().Trim();
        if (string.IsNullOrEmpty(action))
            return EToolResult.Failure(Name, "Missing 'action' argument.");

        var result = action switch
        {
            "create" => await DoCreate(arguments, cancellationToken),
            "diff" => await DoDiff(arguments, cancellationToken),
            "patch" => await DoPatch(arguments, cancellationToken),
            "search" => await DoSearch(arguments, cancellationToken),
            "replace-all" => await DoReplaceAll(arguments, cancellationToken),
            "insert" => await DoInsert(arguments, cancellationToken),
            "delete-lines" => await DoDeleteLines(arguments, cancellationToken),
            _ => $"ECodeEditor: Unknown action: {action}"
        };

        if (result.StartsWith("✅"))
            return EToolResult.Success(Name, result);
        if (result.StartsWith("ECodeEditor:") || result.Contains("not found") || result.Contains("Missing"))
            return EToolResult.Failure(Name, result);
        return EToolResult.Success(Name, result);
    }

    private static T ReadCfg<T>(JsonElement? section, string key, T defaultValue)
    {
        if (section.HasValue && section.Value.ValueKind == JsonValueKind.Object)
        {
            if (section.Value.TryGetProperty(key, out var prop))
            {
                try { return prop.Deserialize<T>() ?? defaultValue; } catch { return defaultValue; }
            }
        }
        return defaultValue;
    }

    // ─── Create: create a new file with content ───────────────
    private async Task<string> DoCreate(Dictionary<string, string?> args, CancellationToken ct)
    {
        var file = args.GetValueOrDefault("file")?.Trim();
        var content = args.GetValueOrDefault("content") ?? "";

        if (string.IsNullOrEmpty(file))
            return "ECodeEditor: Missing 'file' argument.";

        var fullPath = ResolvePath(file);

        if (_fileSystem.FileExists(fullPath))
            return $"ECodeEditor: File already exists: {file}. Use action=patch to modify existing files.";

        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !_fileSystem.DirectoryExists(dir))
                _fileSystem.CreateDirectory(dir);

            await Task.Run(() => _fileSystem.WriteFile(fullPath, content));
            return $"✅ Created {file} ({content.Length} chars).\nContent:\n{content}";
        }
        catch (Exception ex)
        {
            return $"ECodeEditor: Failed to create {file}: {ex.Message}";
        }
    }

    // ─── Patch: replace old_text with new_text in a file ─────────
    private async Task<string> DoPatch(Dictionary<string, string?> args, CancellationToken ct)
    {
        var file = args.GetValueOrDefault("file");
        var oldText = args.GetValueOrDefault("old_text");
        var newText = args.GetValueOrDefault("new_text");

        if (string.IsNullOrEmpty(file) || oldText == null || newText == null)
            return "ECodeEditor: Missing file, old_text, or new_text.";

        var fullPath = ResolvePath(file);
        if (!_fileSystem.FileExists(fullPath))
            return $"ECodeEditor: File not found: {file}";

        if (ct.IsCancellationRequested)
            return "ECodeEditor: [CANCELLED] Operation cancelled by user.";

        var content = await Task.Run(() => _fileSystem.ReadFile(fullPath));

        if (!content.Contains(oldText))
        {
            var similar = FindSimilarLines(content, oldText);
            var msg = $"old_text not found in {file}.";
            if (similar.Count > 0)
                msg += $"\nSimilar lines found:\n{string.Join("\n", similar.Take(3))}";
            return msg;
        }

        var count = CountOccurrences(content, oldText);
        if (count > 1)
        {
            return $"old_text found {count} times in {file}. Add more context to make it unique, " +
                $"or use action=replace-all for intentional multi-replacement.";
        }

        var newContent = content.Replace(oldText, newText);
        var diff = GenerateDiff(content, newContent, file);

        await Task.Run(() => _fileSystem.WriteFile(fullPath, newContent));

        return $"✅ Patched {file}\n\nDiff:\n{diff}";
    }

    // ─── Diff: show what would change ─────────────────────────────
    private async Task<string> DoDiff(Dictionary<string, string?> args, CancellationToken ct)
    {
        var file = args.GetValueOrDefault("file");
        var newText = args.GetValueOrDefault("new_text");

        if (string.IsNullOrEmpty(file) || newText == null)
            return "ECodeEditor: Missing file or new_text.";

        var fullPath = ResolvePath(file);
        if (!_fileSystem.FileExists(fullPath))
            return $"ECodeEditor: File not found: {file}";

        var oldContent = await Task.Run(() => _fileSystem.ReadFile(fullPath));
        var diff = GenerateDiff(oldContent, newText, file);

        return $"Diff for {file}:\n\n{diff}";
    }

    // ─── Search: find pattern across files ────────────────────────
    private async Task<string> DoSearch(Dictionary<string, string?> args, CancellationToken ct)
    {
        var pattern = args.GetValueOrDefault("pattern");
        var filter = args.GetValueOrDefault("file_filter") ?? "*.*";

        if (string.IsNullOrEmpty(pattern))
            return "ECodeEditor: Missing 'pattern'.";

        var files = GetFiles(filter);
        var sb = new StringBuilder();
        var totalMatches = 0;

        foreach (var filePath in files.Take(50))
        {
            try
            {
                var content = await Task.Run(() => _fileSystem.ReadFile(filePath));
                var lines = content.Split('\n');
                var relPath = Path.GetRelativePath(_workingDir, filePath);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"  {relPath}:{i + 1}: {lines[i].Trim()}");
                        totalMatches++;
                        if (totalMatches >= 30) break;
                    }
                }
                if (totalMatches >= 30) break;
            }
            catch { }
        }

        if (totalMatches == 0)
            return $"No matches found for '{pattern}' in {filter}.";

        sb.Insert(0, $"Found {totalMatches} match(es) for '{pattern}':\n\n");
        return sb.ToString();
    }

    // ─── Replace-All: replace pattern across files ────────────────
    private async Task<string> DoReplaceAll(Dictionary<string, string?> args, CancellationToken ct)
    {
        var pattern = args.GetValueOrDefault("pattern");
        var replacement = args.GetValueOrDefault("replacement") ?? "";
        var filter = args.GetValueOrDefault("file_filter") ?? "*.*";

        if (string.IsNullOrEmpty(pattern))
            return "ECodeEditor: Missing 'pattern'.";

        var files = GetFiles(filter);
        var modifiedFiles = new List<string>();
        var totalReplacements = 0;

        foreach (var filePath in files.Take(50))
        {
            try
            {
                var content = await Task.Run(() => _fileSystem.ReadFile(filePath));
                if (!content.Contains(pattern)) continue;

                var count = CountOccurrences(content, pattern);
                var newContent = content.Replace(pattern, replacement);
                await Task.Run(() => _fileSystem.WriteFile(filePath, newContent));
                modifiedFiles.Add(Path.GetRelativePath(_workingDir, filePath));
                totalReplacements += count;
            }
            catch { }
        }

        if (modifiedFiles.Count == 0)
            return $"No files contained '{pattern}'.";

        return $"✅ Replaced {totalReplacements} occurrence(s) of '{pattern}' → '{replacement}' in {modifiedFiles.Count} file(s):\n" +
            string.Join("\n", modifiedFiles.Select(f => $"  {f}"));
    }

    // ─── Insert: insert text at specific line ─────────────────────
    private async Task<string> DoInsert(Dictionary<string, string?> args, CancellationToken ct)
    {
        var file = args.GetValueOrDefault("file");
        var lineStr = args.GetValueOrDefault("line");
        var text = args.GetValueOrDefault("text");

        if (string.IsNullOrEmpty(file) || !int.TryParse(lineStr, out var line) || text == null)
            return "ECodeEditor: Missing file, line (number), or text.";

        var fullPath = ResolvePath(file);
        if (!_fileSystem.FileExists(fullPath))
            return $"ECodeEditor: File not found: {file}";

        if (ct.IsCancellationRequested)
            return "ECodeEditor: [CANCELLED] Operation cancelled by user.";

        var content = await Task.Run(() => _fileSystem.ReadFile(fullPath));
        var lines = content.Split('\n').ToList();
        var insertAt = Math.Clamp(line - 1, 0, lines.Count);
        lines.Insert(insertAt, text);
        var newContent = string.Join('\n', lines);
        await Task.Run(() => _fileSystem.WriteFile(fullPath, newContent));

        return $"✅ Inserted text at line {line} in {file}";
    }

    // ─── Delete-Lines: remove a range of lines ──────────────────────
    private async Task<string> DoDeleteLines(Dictionary<string, string?> args, CancellationToken ct)
    {
        var file = args.GetValueOrDefault("file");
        var startStr = args.GetValueOrDefault("start_line");
        var endStr = args.GetValueOrDefault("end_line");

        if (string.IsNullOrEmpty(file) || !int.TryParse(startStr, out var start) || !int.TryParse(endStr, out var end))
            return "ECodeEditor: Missing file, start_line, or end_line (must be numbers).";

        var fullPath = ResolvePath(file);
        if (!_fileSystem.FileExists(fullPath))
            return $"ECodeEditor: File not found: {file}";

        var content = await Task.Run(() => _fileSystem.ReadFile(fullPath));
        var lines = content.Split('\n').ToList();
        var delStart = Math.Clamp(start - 1, 0, lines.Count - 1);
        var delEnd = Math.Clamp(end, delStart + 1, lines.Count);
        var count = delEnd - delStart;
        lines.RemoveRange(delStart, count);
        var newContent = string.Join('\n', lines);
        await Task.Run(() => _fileSystem.WriteFile(fullPath, newContent));

        return $"✅ Deleted {count} line(s) ({start}-{end}) from {file}";
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private string ResolvePath(string file)
    {
        if (Path.IsPathRooted(file)) return file;
        return Path.Combine(_workingDir, file);
    }

    private List<string> GetFiles(string filter)
    {
        var exclude = new[] { Path.DirectorySeparatorChar + "bin", Path.DirectorySeparatorChar + "obj", Path.DirectorySeparatorChar + ".git" };
        return ListFilesRecursive(_workingDir)
            .Where(f => !exclude.Any(ex => f.Contains(ex)))
            .ToList();
    }

    private List<string> ListFilesRecursive(string directory)
    {
        var result = new List<string>();
        var files = _fileSystem.ListFiles(directory, "*");
        result.AddRange(files);

        var subDirs = Directory.GetDirectories(directory);
        foreach (var subDir in subDirs)
            result.AddRange(ListFilesRecursive(subDir));

        return result;
    }

    private int CountOccurrences(string content, string pattern)
    {
        int count = 0, pos = 0;
        while ((pos = content.IndexOf(pattern, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += pattern.Length;
        }
        return count;
    }

    private List<string> FindSimilarLines(string content, string searchText)
    {
        var lines = content.Split('\n');
        var result = new List<string>();
        var searchWords = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3).Take(3).ToHashSet();

        foreach (var line in lines)
        {
            var lineWords = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var matchCount = lineWords.Count(w => searchWords.Any(s => w.Contains(s, StringComparison.OrdinalIgnoreCase)));
            if (matchCount >= 2)
                result.Add($"  {line.Trim()}");
        }
        return result;
    }

    private string GenerateDiff(string old, string newText, string file)
    {
        var oldLines = old.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var newLines = newText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();

        var maxLines = Math.Max(oldLines.Length, newLines.Length);
        var changes = 0;

        for (int i = 0; i < maxLines; i++)
        {
            var oldLine = i < oldLines.Length ? oldLines[i].TrimEnd() : null;
            var newLine = i < newLines.Length ? newLines[i].TrimEnd() : null;

            if (oldLine != newLine)
            {
                changes++;
                if (oldLine != null)
                    sb.AppendLine($"- {i + 1}: {oldLine}");
                if (newLine != null)
                    sb.AppendLine($"+ {i + 1}: {newLine}");
            }
        }

        if (changes == 0)
            sb.AppendLine("(no changes)");

        return sb.ToString();
    }
}