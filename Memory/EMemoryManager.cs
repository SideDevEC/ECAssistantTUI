using static ECAssistant.EColor;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Linq;

namespace ECAssistant.Memory;

/// <summary>
/// Persistent Memory Manager - gives the agent long-term memory across sessions.
/// 
/// This is what bridges the gap between ECAssistant (which forgets everything) 
/// and me (a persistent agent that remembers decisions, patterns, and lessons).
/// 
/// How it works:
/// - Loads "context cards" from disk before each session
/// - Saves key decisions, successes, failures after each task
/// - Agent can query memory to remember what worked/didn't work
/// - Memory grows over time but stays concise (auto-archiving)
/// 
/// Files stored in: ECAssistant/Memory/memories/   (or custom path)
/// File format: .json with structured metadata for easy searching.
/// </summary>
public class EMemoryManager : IDisposable
{
    private readonly string _memoryDir;
    private readonly List<MemoryEntry> _loadedMemories = new();
    private bool _dirty = false;    // True when memory has unsaved changes
    private int _entryCounter = 0;

    /// <summary>Create memory manager with custom or default directory</summary>
    public EMemoryManager(string? dataPath = null)
    {
        var basePath = !string.IsNullOrWhiteSpace(dataPath)
            ? dataPath
            : "Memory";
        _memoryDir = Path.GetFullPath(basePath);
    }

    /// <summary>Load all memory entries from disk at startup</summary>
    public void Load()
    {
        _loadedMemories.Clear();
        _entryCounter = 0;

        if (!Directory.Exists(_memoryDir))
        {
            Directory.CreateDirectory(_memoryDir);
            return;
        }

        var jsonFiles = Directory.GetFiles(_memoryDir, "*.json", SearchOption.TopDirectoryOnly);
        
        foreach (var file in jsonFiles)
        {
            try
            {
                var raw = File.ReadAllText(file);
                var entry = JsonSerializer.Deserialize<MemoryEntry>(raw);
                if (entry != null && !string.IsNullOrEmpty(entry.Key))
                {
                    _loadedMemories.Add(entry);
                    _entryCounter++;
                }
            }
            catch (Exception ex)
            {
                EColor.Tag(Error(), "Memory", $"Failed to load {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        EColor.TagBold(Success(), "Memory", $"Loaded {_loadedMemories.Count} entries from disk.");
    }

    /// <summary>Save any unsaved changes back to disk</summary>
    public void Save()
    {
        if (!_dirty) return;

        foreach (var entry in _loadedMemories)
        {
            var fileName = $"entry_{_entryCounter:D4}_{SanitizeFilename(entry.Key)}.json";
            var filePath = Path.Combine(_memoryDir, fileName);
            
            try
            {
                var options = new JsonSerializerOptions 
                    { 
                        WriteIndented = true,
                        DefaultIgnoreCondition = JsonIgnoreCondition.Never 
                    };
                File.WriteAllText(filePath, JsonSerializer.Serialize(entry, options));
            }
            catch (Exception ex)
            {
                EColor.Tag(Error(), "Memory", $"Save failed for '{entry.Key}': {ex.Message}");
            }
        }

        _dirty = false;
        EColor.TagBold(Success(), "Memory", "All changes saved to disk.");
    }

    /// <summary>Add a memory entry - key facts, decisions, or lessons learned</summary>
    public void AddEntry(string key, string content, string category = "general", string? relatedProject = null)
    {
        var entry = new MemoryEntry
        {
            Id = _loadedMemories.Count + 1,
            Key = key,
            Content = content,
            Category = category,
            RelatedProject = relatedProject ?? "",
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Confidence = 0.8f,
        };

        _loadedMemories.Add(entry);
        _dirty = true;

        EColor.Tag(Success(), "Memory", $"Saved: {key} (category: {category})");
    }

    /// <summary>Query memory by keyword or category - returns matching entries</summary>
    /// <summary>Query memory with relevance scoring — ranks entries by how well they match.</summary>
    public string Query(string searchTerm, string? categoryFilter = null, int maxResults = 5)
    {
        if (_loadedMemories.Count == 0)
            return "(No memories found)";

        var terms = searchTerm.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Score each entry by relevance
        var scored = _loadedMemories.Select(e =>
        {
            var score = 0.0;
            var keyLower = e.Key.ToLower();
            var contentLower = e.Content.ToLower();

            foreach (var term in terms)
            {
                if (keyLower.Contains(term)) score += 3.0;
                if (contentLower.Contains(term)) score += 1.0;
                if (keyLower.Split(' ', '_', '-').Any(w => w == term)) score += 2.0;
                score *= e.Confidence;
            }

            if (!string.IsNullOrEmpty(categoryFilter))
            {
                if (e.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
                    score *= 1.5;
                else
                    score *= 0.1;
            }

            return (Entry: e, Score: score);
        })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .Take(maxResults)
        .ToList();

        if (scored.Count == 0)
            return $"(No memories found for: {searchTerm})";

        var sb = new StringBuilder();
        sb.AppendLine("### RELEVANT MEMORIES:");
        sb.AppendLine();

        foreach (var (entry, score) in scored)
        {
            sb.Append($"[{entry.Category}] ");
            sb.AppendLine(entry.Key);
            sb.AppendLine($"  (relevance: {score:F1})");
            sb.AppendLine(entry.Content.Substring(0, Math.Min(entry.Content.Length, 500)));
            if (entry.Content.Length > 500) 
                sb.Append("...");
            sb.AppendLine("\n");
        }

        return sb.ToString();
    }

    /// <summary>Count of loaded memories - for startup diagnostics.</summary>
    public int Count => _loadedMemories.Count;

    /// <summary>All entries as enumerable - used by EAgentEngine for counting.</summary>
    public IEnumerable<MemoryEntry> Entries => _loadedMemories.AsEnumerable();

    /// <summary>Get all memory entries as a single string (for LLM context injection)</summary>
    public string GetContextSummary()
    {
        if (_loadedMemories.Count == 0)
            return "(No prior memories - first time working on this project.)";

        var sb = new StringBuilder();
        sb.AppendLine("# PERSISTENT MEMORY\n");
        sb.AppendLine($"This agent has saved {_loadedMemories.Count} memories from previous sessions:\n");

        // Group by category for better organization
        var categories = _loadedMemories.GroupBy(e => e.Category).OrderBy(g => g.Key);
        
        foreach (var group in categories)
        {
            sb.Append($"## Category: {group.Key} ({group.Count()} entries)\n");
            sb.AppendLine();

            foreach (var entry in group.Take(10)) // Limit per category
            {
                sb.Append($"- **{entry.Key}**: ");
                sb.AppendLine(entry.Content.Substring(0, Math.Min(entry.Content.Length, 100)));
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Clear all memory entries (use sparingly)</summary>
    public void Clear()
    {
        _loadedMemories.Clear();
        _dirty = true;
        EColor.TagBold(Info(), "Memory", "All memories cleared. Save to persist deletion.");
    }

    /// <summary>Delete a specific entry by key</summary>
    public void DeleteEntry(string key)
    {
        var entry = _loadedMemories.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            _loadedMemories.Remove(entry);
            _dirty = true;
            EColor.Tag(Info(), "Memory", $"Deleted: {key}");
        }
        else
        {
            EColor.Tag(Error(), "Memory", $"Entry not found: {key}");
        }
    }

    /// <summary>Get memory statistics</summary>
    public string GetStats()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Memory Stats\n");
        sb.AppendLine($"Total entries: {_loadedMemories.Count}");
        sb.AppendLine($"Working directory: {_memoryDir}");
        sb.AppendLine();

        // Category breakdown
        var categories = _loadedMemories.GroupBy(e => e.Category).OrderBy(g => g.Key);
        foreach (var group in categories)
        {
            sb.AppendLine($"- {group.Key}: {group.Count()} entries");
        }

        return sb.ToString();
    }

    /// <summary>Sanitize filename to remove invalid characters</summary>
    private static string SanitizeFilename(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToCharArray())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ')
                sb.Append(c);
            else
                sb.Append('_');
            
            if (sb.Length > 50) break; // Truncate at 50 chars
        }
        return sb.ToString().Trim() + "_";
    }

    /// <summary>Dispose - ensure any pending changes are saved</summary>
    public void Dispose()
    {
        Save();
        EColor.TagBold(Info(), "Memory", "Memory manager disposed.");
    }
}

/// <summary>A single memory entry - key, content, category, metadata</summary>
public class MemoryEntry
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "general";
    public string RelatedProject { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public float Confidence { get; set; } = 0.8f;

    public override string ToString()
    {
        return $"[{Category}] {Key}: {Content.Substring(0, Math.Min(Content.Length, 100))}...";
    }
}
