using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Services;

namespace ECAssistant.Tools.Code;

/// <summary>
/// Code Editor Tool — surgical code edits with diff preview, multi-line replacement,
/// cross-file search & replace, and syntax-aware editing.
/// 
/// Actions:
///   diff       — show diff between current file and new content
///   patch      — apply a multi-line patch (old_text → new_text)
///   search     — find pattern across all files
///   replace-all— replace pattern across all files
///   insert     — insert text at a specific line number
///   delete-lines — delete a range of lines
/// </summary>
public class ECodeEditorTool : EToolBase
{
    private readonly string _workingDir;

    public ECodeEditorTool(string workingDir) => _workingDir = workingDir;

    public override string Name => "ECodeEditor";

    public override string Description =>
        "Surgical code editing: create files, multi-line patch, diff preview, cross-file search & replace, " +
        "line insertion/deletion. Better than shell echo for code changes.";

    public override string UsageExample =>
        "ECodeEditor(action=\"create\", file=\"Program.cs\", content=\"code here\")  or  ECodeEditor(action=\"patch\", file=\"Program.cs\", old_text=\"old\", new_text=\"new\")";

    public override string GetToolRules() =>
        "create: <file>+<content> (creates new file, fails if exists). " +
        "patch: <file>+<old_text>+<new_text> (multi-line, unique match). " +
        "search: <pattern>+<file_filter>. replace-all: <pattern>+<replacement>+<file_filter>. " +
        "insert: <file>+<line>+<text>. delete-lines: <file>+<start_line>+<end_line>.";


    public override string GetToolExample() =>
        "<toolcall>ECodeEditor<action>create</action><file>new.txt</file><content>hello</content></toolcall>\n" +
        "<toolcall>ECodeEditor<action>patch</action><file>Program.cs</file><old_text>var x=1;</old_text><new_text>var x=2;</new_text></toolcall>\n" +
        "<toolcall>ECodeEditor<action>search</action><pattern>TODO</pattern></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var action = arguments.GetValueOrDefault("action")?.ToLower().Trim();
        if (string.IsNullOrEmpty(action))
            return EToolResult.Failure(Name, "Missing 'action' argument.");

        return action switch
        {
            "create" => await DoCreate(arguments, cancellationToken),
            "diff" => await DoDiff(arguments, cancellationToken),
            "patch" => await DoPatch(arguments, cancellationToken),
            "search" => await DoSearch(arguments, cancellationToken),
            "replace-all" => await DoReplaceAll(arguments, cancellationToken),
            "insert" => await DoInsert(arguments, cancellationToken),
            "delete-lines" => await DoDeleteLines(arguments, cancellationToken),
            _ => EToolResult.Failure(Name, $"Unknown action: {action}")
        };
    }

    // ─── Create: create a new file with content ───────────────
    private async Task<EToolResult> DoCreate(Dictionary<string, string?> args, CancellationToken ct)
    {
        var file = args.GetValueOrDefault("file")?.Trim();
        var content = args.GetValueOrDefault("content") ?? "";

        if (string.IsNullOrEmpty(file))
            return EToolResult.Failure(Name, "Missing 'file' argument.");

        var fullPath = Path.Combine(_workingDir, file);

        if (File.Exists(fullPath))
            return EToolResult.Failure(Name, $"File already exists: {file}. Use action=patch to modify existing files.");

        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, content, ct);
            Logger.Info(Name, $"Created: {file} ({content.Length} chars)");
            return EToolResult.Success(Name, $"✅ Created {file} ({content.Length} chars).\nContent:\n{content}");
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Failed to create {file}: {ex.Message}");
        }
    }

    // ─── Patch: replace old_text with new_text in a file ─────────

    private async Task<EToolResult> DoPatch(Dictionary<string, string?> args, CancellationToken ct)
    {
        var file = args.GetValueOrDefault("file");
        var oldText = args.GetValueOrDefault("old_text");
        var newText = args.GetValueOrDefault("new_text");

        if (string.IsNullOrEmpty(file) || oldText == null || newText == null)
            return EToolResult.Failure(Name, "Missing file, old_text, or new_text.");

        var fullPath = ResolvePath(file);
        if (!File.Exists(fullPath))
            return EToolResult.Failure(Name, $"File not found: {file}");

        if (ct.IsCancellationRequested) return EToolResult.Failure(Name, "[CANCELLED] Operation cancelled by user.");
            var content = await File.ReadAllTextAsync(fullPath, ct);

        if (!content.Contains(oldText))
        {
            // Try to find close match for helpful error
            var similar = FindSimilarLines(content, oldText);
            var msg = $"old_text not found in {file}.";
            if (similar.Count > 0)
                msg += $"\nSimilar lines found:\n{string.Join("\n", similar.Take(3))}";
            return EToolResult.Failure(Name, msg);
        }

        // Count occurrences — warn if multiple
        var count = CountOccurrences(content, oldText);
        if (count > 1)
        {
            return EToolResult.Failure(Name,
                $"old_text found {count} times in {file}. Add more context to make it unique, " +
                $"or use action=replace-all for intentional multi-replacement.");
        }

        // Apply patch
        var newContent = content.Replace(oldText, newText);

        // Generate diff
        var diff = GenerateDiff(content, newContent, file);

        await File.WriteAllTextAsync(fullPath, newContent, ct);

        Logger.Info("CodeEditor", $"Patched {file}: {oldText.Length} chars → {newText.Length} chars");

        return EToolResult.Success(Name,
            $"✅ Patched {file}\n\nDiff:\n{diff}",
            new Dictionary<string, string> { ["file"] = file, ["action"] = "patch" });
    }

    // ─── Diff: show what would change ─────────────────────────────

    private async Task<EToolResult> DoDiff(Dictionary<string, string?> args, CancellationToken ct)
    {
        var file = args.GetValueOrDefault("file");
        var newText = args.GetValueOrDefault("new_text");

        if (string.IsNullOrEmpty(file) || newText == null)
            return EToolResult.Failure(Name, "Missing file or new_text.");

        var fullPath = ResolvePath(file);
        if (!File.Exists(fullPath))
            return EToolResult.Failure(Name, $"File not found: {file}");

        var oldContent = await File.ReadAllTextAsync(fullPath, ct);
        var diff = GenerateDiff(oldContent, newText, file);

        return EToolResult.Success(Name, $"Diff for {file}:\n\n{diff}");
    }

    // ─── Search: find pattern across files ────────────────────────

    private async Task<EToolResult> DoSearch(Dictionary<string, string?> args, CancellationToken ct)
    {
        var pattern = args.GetValueOrDefault("pattern");
        var filter = args.GetValueOrDefault("file_filter") ?? "*.*";

        if (string.IsNullOrEmpty(pattern))
            return EToolResult.Failure(Name, "Missing 'pattern'.");

        var files = GetFiles(filter);
        var sb = new StringBuilder();
        var totalMatches = 0;

        foreach (var filePath in files.Take(50)) // limit
        {
            try
            {
                var content = await File.ReadAllTextAsync(filePath, ct);
                var lines = content.Split('\n');
                var relPath = Path.GetRelativePath(_workingDir, filePath);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"  {relPath}:{i + 1}: {lines[i].Trim()}");
                        totalMatches++;
                        if (totalMatches >= 30) break; // limit results
                    }
                }
                if (totalMatches >= 30) break;
            }
            catch { }
        }

        if (totalMatches == 0)
            return EToolResult.Success(Name, $"No matches found for '{pattern}' in {filter}.");

        sb.Insert(0, $"Found {totalMatches} match(es) for '{pattern}':\n\n");
        return EToolResult.Success(Name, sb.ToString());
    }

    // ─── Replace-All: replace pattern across files ────────────────

    private async Task<EToolResult> DoReplaceAll(Dictionary<string, string?> args, CancellationToken ct)
    {
        var pattern = args.GetValueOrDefault("pattern");
        var replacement = args.GetValueOrDefault("replacement") ?? "";
        var filter = args.GetValueOrDefault("file_filter") ?? "*.*";

        if (string.IsNullOrEmpty(pattern))
            return EToolResult.Failure(Name, "Missing 'pattern'.");

        var files = GetFiles(filter);
        var modifiedFiles = new List<string>();
        var totalReplacements = 0;

        foreach (var filePath in files.Take(50))
        {
            try
            {
                var content = await File.ReadAllTextAsync(filePath);
                if (!content.Contains(pattern)) continue;

                var count = CountOccurrences(content, pattern);
                var newContent = content.Replace(pattern, replacement);
                await File.WriteAllTextAsync(filePath, newContent, ct);
                modifiedFiles.Add(Path.GetRelativePath(_workingDir, filePath));
                totalReplacements += count;
            }
            catch { }
        }

        if (modifiedFiles.Count == 0)
            return EToolResult.Success(Name, $"No files contained '{pattern}'.");

        return EToolResult.Success(Name,
            $"✅ Replaced {totalReplacements} occurrence(s) of '{pattern}' → '{replacement}' in {modifiedFiles.Count} file(s):\n" +
            string.Join("\n", modifiedFiles.Select(f => $"  {f}")),
            new Dictionary<string, string> { ["files_modified"] = modifiedFiles.Count.ToString(), ["replacements"] = totalReplacements.ToString() });
    }

    // ─── Insert: insert text at specific line ─────────────────────

    private async Task<EToolResult> DoInsert(Dictionary<string, string?> args, CancellationToken ct)
    {
        var file = args.GetValueOrDefault("file");
        var lineStr = args.GetValueOrDefault("line");
        var text = args.GetValueOrDefault("text");

        if (string.IsNullOrEmpty(file) || !int.TryParse(lineStr, out var line) || text == null)
            return EToolResult.Failure(Name, "Missing file, line (number), or text.");

        var fullPath = ResolvePath(file);
        if (!File.Exists(fullPath))
            return EToolResult.Failure(Name, $"File not found: {file}");

        if (ct.IsCancellationRequested) return EToolResult.Failure(Name, "[CANCELLED] Operation cancelled by user.");
            var lines = (await File.ReadAllLinesAsync(fullPath, ct)).ToList();
        var insertAt = Math.Clamp(line - 1, 0, lines.Count); // 1-indexed to 0-indexed
        lines.Insert(insertAt, text);
        await File.WriteAllLinesAsync(fullPath, lines);

        Logger.Info("CodeEditor", $"Inserted at line {line} in {file}");
        return EToolResult.Success(Name, $"✅ Inserted text at line {line} in {file}");
    }

    // ─── Delete-Lines: remove a range of lines ──────────────────────

    private async Task<EToolResult> DoDeleteLines(Dictionary<string, string?> args, CancellationToken ct)
    {
        var file = args.GetValueOrDefault("file");
        var startStr = args.GetValueOrDefault("start_line");
        var endStr = args.GetValueOrDefault("end_line");

        if (string.IsNullOrEmpty(file) || !int.TryParse(startStr, out var start) || !int.TryParse(endStr, out var end))
            return EToolResult.Failure(Name, "Missing file, start_line, or end_line (must be numbers).");

        var fullPath = ResolvePath(file);
        if (!File.Exists(fullPath))
            return EToolResult.Failure(Name, $"File not found: {file}");

        var lines = (await File.ReadAllLinesAsync(fullPath)).ToList();
        var delStart = Math.Clamp(start - 1, 0, lines.Count - 1);
        var delEnd = Math.Clamp(end, delStart + 1, lines.Count);
        var count = delEnd - delStart;
        lines.RemoveRange(delStart, count);
        await File.WriteAllLinesAsync(fullPath, lines);

        Logger.Info("CodeEditor", $"Deleted lines {start}-{end} from {file}");
        return EToolResult.Success(Name, $"✅ Deleted {count} line(s) ({start}-{end}) from {file}");
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private string ResolvePath(string file)
    {
        if (Path.IsPathRooted(file)) return file;
        return Path.Combine(_workingDir, file);
    }

    private List<string> GetFiles(string filter)
    {
        // Skip bin/obj/.git
        var exclude = new[] { Path.DirectorySeparatorChar + "bin", Path.DirectorySeparatorChar + "obj", Path.DirectorySeparatorChar + ".git" };
        return Directory.GetFiles(_workingDir, filter, SearchOption.AllDirectories)
            .Where(f => !exclude.Any(ex => f.Contains(ex)))
            .ToList();
    }

    private static int CountOccurrences(string content, string pattern)
    {
        int count = 0, pos = 0;
        while ((pos = content.IndexOf(pattern, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += pattern.Length;
        }
        return count;
    }

    private static List<string> FindSimilarLines(string content, string searchText)
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

    private static string GenerateDiff(string old, string newText, string file)
    {
        var oldLines = old.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var newLines = newText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();

        // Simple line-by-line diff
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