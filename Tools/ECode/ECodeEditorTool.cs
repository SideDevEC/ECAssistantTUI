using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Code;

/// <summary>
/// Code Editor Tool — surgical code edits with diff preview, multi-line replacement,
/// cross-file search & replace, and syntax-aware editing.
/// 
/// Actions:
///   create       — create a new file with content
///   diff         — show diff between current file and new content
///   patch        — apply a multi-line patch (old_text → new_text)
///   search       — find pattern across all files
///   replace-all  — replace pattern across all files
///   insert       — insert text at a specific line number
///   delete-lines — delete a range of lines
/// </summary>
public class ECodeEditorTool : ITool
{
    private readonly IFileSystem _fileSystem;
    private readonly string _workingDir;

    public ECodeEditorTool(IFileSystem fileSystem, IConfigProvider configProvider)
    {
        _fileSystem = fileSystem;
        _workingDir = configProvider.GetValue("workingDir", Directory.GetCurrentDirectory());
    }

    public string Name => "ECodeEditor";

    public string Description =>
        "Surgical code editing: create files, multi-line patch, diff preview, cross-file search & replace, " +
        "line insertion/deletion. Better than shell echo for code changes.";

    public Task<string> ExecuteAsync(string input, CancellationToken ct = default)
    {
        return ExecuteCore(ParseInput(input), ct);
    }

    public Interfaces.ToolPolicy GetPolicy() => Interfaces.ToolPolicy.Approved(Name);

    private async Task<string> ExecuteCore(Dictionary<string, string?> arguments, CancellationToken ct)
    {
        var action = arguments.GetValueOrDefault("action")?.ToLower().Trim();
        if (string.IsNullOrEmpty(action))
            return $"ECodeEditor: Missing 'action' argument.";

        return action switch
        {
            "create" => await DoCreate(arguments, ct),
            "diff" => await DoDiff(arguments, ct),
            "patch" => await DoPatch(arguments, ct),
            "search" => await DoSearch(arguments, ct),
            "replace-all" => await DoReplaceAll(arguments, ct),
            "insert" => await DoInsert(arguments, ct),
            "delete-lines" => await DoDeleteLines(arguments, ct),
            _ => $"ECodeEditor: Unknown action: {action}"
        };
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

        // Count occurrences — warn if multiple
        var count = CountOccurrences(content, oldText);
        if (count > 1)
        {
            return $"old_text found {count} times in {file}. Add more context to make it unique, " +
                $"or use action=replace-all for intentional multi-replacement.";
        }

        // Apply patch
        var newContent = content.Replace(oldText, newText);

        // Generate diff
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
        {
            result.AddRange(ListFilesRecursive(subDir));
        }

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

    private Dictionary<string, string?> ParseInput(string input)
    {
        var args = new Dictionary<string, string?>();
        var matches = Regex.Matches(input, @"<(\w+)>(.*?)</\1>");
        foreach (Match match in matches)
            args[match.Groups[1].Value] = match.Groups[2].Value;
        return args;
    }
}