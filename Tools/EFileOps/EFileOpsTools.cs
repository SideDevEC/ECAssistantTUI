using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Tools;

namespace ECAssistant.Tools.FileOps;

/// <summary>
/// File Read Tool — reads file contents with optional offset/limit.
/// Safe operation (read-only), no approval needed.
/// </summary>
public class EFileReadTool : EToolBase
{
    private readonly string _workingDirectory;

    public EFileReadTool(string workingDirectory)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public override string Name => "EFileRead";

    public override string Description =>
        "Read the contents of a file. Supports line offset and limit for large files. " +
        "Use for: inspecting source code, reading config files, checking file contents before editing.";

    public override string UsageExample => "EFileRead(path=\"Program.cs\")";

    public override string GetToolRules() =>
        "RULE: <path> is a relative path from the working directory. " +
        "Optional <offset> = starting line number (1-based, default 1). " +
        "Optional <limit> = max lines to read (default 2000).";

    public override string GetToolExample() =>
        "<toolcall>EFileRead<path>src/Program.cs</path></toolcall>\n" +
        "<toolcall>EFileRead<path>config.json</path><offset>50</offset><limit>100</limit></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments)
    {
        // Accept multiple arg names: path, file, filepath, filename
        var path = arguments.GetValueOrDefault("path")
                   ?? arguments.GetValueOrDefault("file")
                   ?? arguments.GetValueOrDefault("filepath")
                   ?? arguments.GetValueOrDefault("filename");
        if (string.IsNullOrWhiteSpace(path))
            return EToolResult.Failure(Name, "Missing 'path' argument.");

        var offset = 1;
        if (arguments.TryGetValue("offset", out var offStr) && int.TryParse(offStr, out var o))
            offset = Math.Max(1, o);

        var limit = 2000;
        if (arguments.TryGetValue("limit", out var limStr) && int.TryParse(limStr, out var l))
            limit = Math.Max(1, l);

        // Resolve path relative to working directory
        var fullPath = ResolvePath(path!);

        if (!File.Exists(fullPath))
        {
            // Try workspace directory as fallback
            var workspacePath = Path.Combine(_workingDirectory, "Workspace", path!);
            if (File.Exists(workspacePath))
                fullPath = workspacePath;
            else
                return EToolResult.Failure(Name, $"File not found: {path} (resolved: {fullPath}). " +
                    $"Working directory: {_workingDirectory}. " +
                    "Use EDirList to see available files, or provide a correct relative path.");
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(fullPath);
            var totalLines = lines.Length;

            var startIdx = Math.Min(offset - 1, totalLines);
            var endIdx = Math.Min(startIdx + limit, totalLines);
            var readCount = endIdx - startIdx;

            var sb = new StringBuilder();
            sb.AppendLine($"File: {path} ({totalLines} lines total, showing {readCount} from line {offset})");
            sb.AppendLine("<detail>");

            for (int i = startIdx; i < endIdx; i++)
            {
                // Escape angle brackets to prevent XML tag confusion in LLM history
                var line = lines[i]
                    .Replace("\u003c", "&lt;")
                    .Replace("\u003e", "&gt;");
                sb.AppendLine($"{i + 1,4} | {line}");
            }

            sb.AppendLine("</detail>");

            if (endIdx < totalLines)
                sb.AppendLine($"... ({totalLines - endIdx} more lines, use offset={endIdx + 1} to continue)");

            return EToolResult.Success(Name, sb.ToString(), new Dictionary<string, string>
            {
                ["total_lines"] = totalLines.ToString(),
                ["lines_read"] = readCount.ToString(),
                ["offset"] = offset.ToString()
            });
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Error reading file: {ex.Message}");
        }
    }

    private string ResolvePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return relativePath;
        return Path.GetFullPath(Path.Combine(_workingDirectory, relativePath));
    }
}

/// <summary>
/// File Write Tool — creates or overwrites a file.
/// Write operation: logged but does not require approval (the agent should be able to create files).
/// </summary>
public class EFileWriteTool : EToolBase
{
    private readonly string _workingDirectory;

    public EFileWriteTool(string workingDirectory)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public override string Name => "EFileWrite";

    public override string Description =>
        "Create or overwrite a file with the given content. " +
        "Automatically creates parent directories. " +
        "Use for: creating new files, writing generated code, saving output.";

    public override string UsageExample => "EFileWrite(path=\"output.txt\", content=\"Hello World\")";

    public override string GetToolRules() =>
        "RULE: <path> is relative to working directory. " +
        "<content> contains the ENTIRE file content — this overwrites existing files. " +
        "Always read a file first before overwriting it.";

    public override string GetToolExample() =>
        "<toolcall>EFileWrite<path>workspace/notes.md</path><content># Notes\nThis is a test file.</content></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments)
    {
        // Accept multiple arg names: path, file, filepath, filename
        var path = arguments.GetValueOrDefault("path")
                   ?? arguments.GetValueOrDefault("file")
                   ?? arguments.GetValueOrDefault("filepath")
                   ?? arguments.GetValueOrDefault("filename");
        var content = arguments.GetValueOrDefault("content");

        if (string.IsNullOrWhiteSpace(path))
            return EToolResult.Failure(Name, "Missing 'path' argument.");
        if (content == null)
            return EToolResult.Failure(Name, "Missing 'content' argument.");

        var fullPath = ResolvePath(path!);

        // Sandbox check — don't allow writing outside working directory
        if (!IsWithinDirectory(fullPath, _workingDirectory))
            return EToolResult.Failure(Name, $"Path outside working directory: {path}");

        try
        {
            // Create parent directories if needed
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var existed = File.Exists(fullPath);
            await File.WriteAllTextAsync(fullPath, content);

            var lines = content.Split('\n').Length;
            return EToolResult.Success(Name,
                $"File {(existed ? "overwritten" : "created")}: {path} ({content.Length} chars, {lines} lines)",
                new Dictionary<string, string>
                {
                    ["existed"] = existed.ToString(),
                    ["bytes"] = content.Length.ToString(),
                    ["lines"] = lines.ToString()
                });
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Error writing file: {ex.Message}");
        }
    }

    private string ResolvePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return relativePath;
        return Path.GetFullPath(Path.Combine(_workingDirectory, relativePath));
    }

    private static bool IsWithinDirectory(string fullPath, string baseDir)
    {
        var fullDir = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// File Edit Tool — precise text replacement within a file.
/// Supports multiple edits in one call. Each edit must match exactly once in the file.
/// </summary>
public class EFileEditTool : EToolBase
{
    private readonly string _workingDirectory;

    public EFileEditTool(string workingDirectory)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public override string Name => "EFileEdit";

    public override string Description =>
        "Make precise text replacements in a file. Each edit finds exact text and replaces it. " +
        "Supports multiple edits in one call via repeated <old> and <new> tags. " +
        "Use for: targeted code changes, fixing bugs, updating config values.";

    public override string UsageExample => "EFileEdit(path=\"Program.cs\", old=\"var x = 1;\", new=\"var x = 2;\")";

    public override string GetToolRules() =>
        "RULE: <path> is relative to working directory. " +
        "<old> contains the EXACT text to find (must be unique in the file). " +
        "<new> contains the replacement text. " +
        "For multiple edits, use multiple <old1>/<new1>, <old2>/<new2> pairs. " +
        "ALWAYS read the file first to verify the exact text before editing.";

    public override string GetToolExample() =>
        "<toolcall>EFileEdit<path>Program.cs</path><old>Console.WriteLine(\"Hello\");</old><new>Console.WriteLine(\"Hello World\");</new></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments)
    {
        // Accept multiple arg names: path, file, filepath, filename
        var path = arguments.GetValueOrDefault("path")
                   ?? arguments.GetValueOrDefault("file")
                   ?? arguments.GetValueOrDefault("filepath")
                   ?? arguments.GetValueOrDefault("filename");
        if (string.IsNullOrWhiteSpace(path))
            return EToolResult.Failure(Name, "Missing 'path' argument.");

        var fullPath = ResolvePath(path!);
        if (!File.Exists(fullPath))
            return EToolResult.Failure(Name, $"File not found: {path}");

        if (!IsWithinDirectory(fullPath, _workingDirectory))
            return EToolResult.Failure(Name, $"Path outside working directory: {path}");

        try
        {
            var content = await File.ReadAllTextAsync(fullPath);
            var editsMade = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"Editing: {path}");

            // Collect all old/new pairs
            var pairs = new List<(string old, string @new)>();
            var primaryOld = arguments.GetValueOrDefault("old");
            var primaryNew = arguments.GetValueOrDefault("new");
            if (primaryOld != null && primaryNew != null)
                pairs.Add((primaryOld, primaryNew));

            // Numbered pairs: old1/new1, old2/new2, etc.
            for (int i = 1; i <= 20; i++)
            {
                var oldKey = $"old{i}";
                var newKey = $"new{i}";
                if (arguments.TryGetValue(oldKey, out var o) && arguments.TryGetValue(newKey, out var n)
                    && o != null && n != null)
                    pairs.Add((o, n));
            }

            if (pairs.Count == 0)
                return EToolResult.Failure(Name, "No edits provided. Use <old>/<new> or <old1>/<new1> pairs.");

            foreach (var (oldText, newText) in pairs)
            {
                var count = CountOccurrences(content, oldText);
                if (count == 0)
                {
                    sb.AppendLine($"  ✗ NOT FOUND: \"{Truncate(oldText, 80)}\"");
                    continue;
                }
                if (count > 1)
                {
                    sb.AppendLine($"  ✗ NOT UNIQUE ({count} matches): \"{Truncate(oldText, 80)}\"");
                    continue;
                }

                content = content.Replace(oldText, newText);
                editsMade++;
                sb.AppendLine($"  ✓ Replaced: \"{Truncate(oldText, 60)}\" → \"{Truncate(newText, 60)}\"");
            }

            if (editsMade > 0)
            {
                await File.WriteAllTextAsync(fullPath, content);
                sb.AppendLine($"\n{editsMade} edit(s) applied successfully. File saved.");
            }
            else
            {
                sb.AppendLine("\nNo edits applied (all searches failed).");
            }

            return EToolResult.Success(Name, sb.ToString(), new Dictionary<string, string>
            {
                ["edits_made"] = editsMade.ToString(),
                ["edits_requested"] = pairs.Count.ToString()
            });
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Error editing file: {ex.Message}");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, pos = 0;
        while ((pos = haystack.IndexOf(needle, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += needle.Length;
        }
        return count;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";

    private string ResolvePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return relativePath;
        return Path.GetFullPath(Path.Combine(_workingDirectory, relativePath));
    }

    private static bool IsWithinDirectory(string fullPath, string baseDir)
    {
        var fullDir = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Directory List Tool — lists files and directories.
/// Read-only, safe operation.
/// </summary>
public class EDirListTool : EToolBase
{
    private readonly string _workingDirectory;

    public EDirListTool(string workingDirectory)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public override string Name => "EDirList";

    public override string Description =>
        "List files and directories in a given path. " +
        "Shows file sizes, modification dates, and file types. " +
        "Use for: exploring project structure, finding files, checking what exists.";

    public override string UsageExample => "EDirList(path=\".\", recursive=\"false\")";

    public override string GetToolRules() =>
        "RULE: <path> is relative to working directory (default: \".\"). " +
        "Optional <recursive> = \"true\" or \"false\" (default: false). " +
        "Optional <pattern> = glob pattern like \"*.cs\" (default: \"*.*\").";

    public override string GetToolExample() =>
        "<toolcall>EDirList<path>.</path></toolcall>\n" +
        "<toolcall>EDirList<path>src</path><recursive>true</recursive><pattern>*.cs</pattern></toolcall>";

    public override Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments)
    {
        var path = arguments.GetValueOrDefault("path") ?? ".";
        var recursive = arguments.GetValueOrDefault("recursive")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        var pattern = arguments.GetValueOrDefault("pattern") ?? "*.*";

        var fullPath = ResolvePath(path);
        if (!Directory.Exists(fullPath))
            return Task.FromResult(EToolResult.Failure(Name, $"Directory not found: {path}"));

        try
        {
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(fullPath, pattern, searchOption);
            var dirs = Directory.GetDirectories(fullPath, "*", searchOption);

            var sb = new StringBuilder();
            sb.AppendLine($"Directory: {path} ({files.Length} files, {dirs.Length} subdirs{(recursive ? ", recursive" : "")})");
            sb.AppendLine();

            // Show directories first
            foreach (var dir in dirs.Take(50))
            {
                var relPath = Path.GetRelativePath(fullPath, dir);
                sb.AppendLine($"  📁 {relPath}/");
            }

            // Then files
            foreach (var file in files.Take(200))
            {
                var relPath = Path.GetRelativePath(fullPath, file);
                var info = new FileInfo(file);
                sb.AppendLine($"  📄 {relPath} ({FormatSize(info.Length)})");
            }

            if (files.Length > 200)
                sb.AppendLine($"  ... and {files.Length - 200} more files");

            return Task.FromResult(EToolResult.Success(Name, sb.ToString(), new Dictionary<string, string>
            {
                ["file_count"] = files.Length.ToString(),
                ["dir_count"] = dirs.Length.ToString()
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(EToolResult.Failure(Name, $"Error listing directory: {ex.Message}"));
        }
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes}B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024}KB" :
        $"{bytes / (1024 * 1024)}MB";

    private string ResolvePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return relativePath;
        return Path.GetFullPath(Path.Combine(_workingDirectory, relativePath));
    }
}

/// <summary>
/// File Copy Tool — copies a file to a new location.
/// Write operation: sandboxed to working directory.
/// </summary>
public class EFileCopyTool : EToolBase
{
    private readonly string _workingDirectory;

    public EFileCopyTool(string workingDirectory)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public override string Name => "EFileCopy";

    public override string Description =>
        "Copy a file to a new location. Creates parent directories if needed. " +
        "Use for: duplicating files, backing up, creating copies with new names.";

    public override string UsageExample => "EFileCopy(source=\"Program.cs\", dest=\"Program_backup.cs\")";

    public override string GetToolRules() =>
        "RULE: <source> is the file to copy (relative to working dir). " +
        "<dest> is the destination path (relative to working dir). " +
        "Both must be within the working directory.";

    public override string GetToolExample() =>
        "<toolcall>EFileCopy<source>Program.cs</source><dest>Program_backup.cs</dest></toolcall>";

    public override Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments)
    {
        var source = arguments.GetValueOrDefault("source")
                   ?? arguments.GetValueOrDefault("src")
                   ?? arguments.GetValueOrDefault("from");
        var dest = arguments.GetValueOrDefault("dest")
                 ?? arguments.GetValueOrDefault("destination")
                 ?? arguments.GetValueOrDefault("to")
                 ?? arguments.GetValueOrDefault("target");

        if (string.IsNullOrWhiteSpace(source))
            return Task.FromResult(EToolResult.Failure(Name, "Missing 'source' argument."));
        if (string.IsNullOrWhiteSpace(dest))
            return Task.FromResult(EToolResult.Failure(Name, "Missing 'dest' argument."));

        var fullSource = ResolvePath(source!);
        var fullDest = ResolvePath(dest!);

        if (!File.Exists(fullSource))
            return Task.FromResult(EToolResult.Failure(Name, $"Source file not found: {source}"));
        if (!IsWithinDirectory(fullDest, _workingDirectory))
            return Task.FromResult(EToolResult.Failure(Name, $"Destination outside working directory: {dest}"));

        try
        {
            var dir = Path.GetDirectoryName(fullDest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.Copy(fullSource, fullDest, overwrite: true);

            var info = new FileInfo(fullDest);
            return Task.FromResult(EToolResult.Success(Name,
                $"Copied: {source} → {dest} ({info.Length} bytes)",
                new Dictionary<string, string>
                {
                    ["source"] = source!,
                    ["dest"] = dest!,
                    ["bytes"] = info.Length.ToString()
                }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(EToolResult.Failure(Name, $"Error copying file: {ex.Message}"));
        }
    }

    private string ResolvePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return relativePath;
        return Path.GetFullPath(Path.Combine(_workingDirectory, relativePath));
    }

    private static bool IsWithinDirectory(string fullPath, string baseDir)
    {
        var fullDir = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// File Search Tool — searches for files by name pattern across the project.
/// Read-only, safe operation.
/// </summary>
public class EFileSearchTool : EToolBase
{
    private readonly string _workingDirectory;

    public EFileSearchTool(string workingDirectory)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public override string Name => "EFileSearch";

    public override string Description =>
        "Search for files by name pattern. Returns matching file paths. " +
        "Use for: finding files, locating configs, searching for specific file types.";

    public override string UsageExample => "EFileSearch(pattern=\"*.cs\", path=\".\")";

    public override string GetToolRules() =>
        "RULE: <pattern> is a glob pattern (e.g., \"*.cs\", \"*Test*\"). " +
        "Optional <path> = search root (default: working directory). " +
        "Optional <content> = if provided, also searches file contents for this text.";

    public override string GetToolExample() =>
        "<toolcall>EFileSearch<pattern>*.cs</pattern></toolcall>\n" +
        "<toolcall>EFileSearch<pattern>*.json</pattern><content>connectionString</content></toolcall>";

    public override Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments)
    {
        var pattern = arguments.GetValueOrDefault("pattern") ?? "*.*";
        var path = arguments.GetValueOrDefault("path") ?? ".";
        var contentSearch = arguments.GetValueOrDefault("content");

        var fullPath = ResolvePath(path);
        if (!Directory.Exists(fullPath))
            return Task.FromResult(EToolResult.Failure(Name, $"Directory not found: {path}"));

        try
        {
            var files = Directory.GetFiles(fullPath, pattern, SearchOption.AllDirectories);
            var sb = new StringBuilder();
            sb.AppendLine($"Found {files.Length} files matching '{pattern}' in {path}");

            var matchedFiles = new List<string>();

            foreach (var file in files.Take(500))
            {
                var relPath = Path.GetRelativePath(_workingDirectory, file);

                if (contentSearch != null)
                {
                    // Content search — check if file contains the search text
                    try
                    {
                        var content = File.ReadAllText(file);
                        if (content.Contains(contentSearch, StringComparison.OrdinalIgnoreCase))
                        {
                            sb.AppendLine($"  📄 {relPath} ✓ (contains \"{contentSearch}\")");
                            matchedFiles.Add(relPath);
                        }
                    }
                    catch
                    {
                        // Skip files we can't read
                    }
                }
                else
                {
                    sb.AppendLine($"  📄 {relPath}");
                    matchedFiles.Add(relPath);
                }
            }

            if (contentSearch != null)
                sb.AppendLine($"\n{matchedFiles.Count} files contain \"{contentSearch}\"");

            if (files.Length > 500)
                sb.AppendLine($"... and {files.Length - 500} more files");

            return Task.FromResult(EToolResult.Success(Name, sb.ToString(), new Dictionary<string, string>
            {
                ["total_matches"] = matchedFiles.Count.ToString(),
                ["files_scanned"] = files.Length.ToString()
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(EToolResult.Failure(Name, $"Error searching: {ex.Message}"));
        }
    }

    private string ResolvePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return relativePath;
        return Path.GetFullPath(Path.Combine(_workingDirectory, relativePath));
    }
}