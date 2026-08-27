using ECAssistant.Core;
using ECAssistant.Core.Setup;
using ECAssistant.TUI.UI;

namespace ECAssistant.TUI.Controller;

/// <summary>
/// First-run model installer wizard for the TUI.
/// Shown when no usable models are detected: lists the editable model-catalog.json
/// entries grouped by category, downloads the selection from HuggingFace with
/// progress display, and wires the chosen models into llm-server.json.
/// </summary>
public sealed class FirstRunWizard
{
    private readonly IGuiConsole _console;
    private readonly EColor _color;
    private readonly ModelCatalogDocument _catalog;
    private readonly ModelInstallerService _installer;
    private readonly FirstRunStatus _status;

    public FirstRunWizard(
        IGuiConsole console,
        EColor color,
        ModelCatalogDocument catalog,
        ModelInstallerService installer,
        FirstRunStatus status)
    {
        _console = console;
        _color = color;
        _catalog = catalog;
        _installer = installer;
        _status = status;
    }

    /// <summary>
    /// Run the wizard interactively. Returns the chosen chat entry id (or null if skipped).
    /// vision/embedding picks are additional and non-blocking.
    /// </summary>
    public async Task<string?> RunAsync(CancellationToken ct = default)
    {
        _console.WriteLineColored(_color.Yellow + _color.Bold +
            "════════ First-Run Setup — no models detected ════════" + _color.Reset);

        var catalogPath = "model-catalog.json";
        _console.WriteLineColored(
            $"The model catalog is at {catalogPath} — edit it anytime to add your own models." +
            _color.Reset);
        _console.BlankLine();

        var selectable = _catalog.Models
            .Where(m => !_status.InstalledEntryIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (selectable.Count == 0)
        {
            _console.WriteLineColored("Nothing left to install in the catalog." + _color.Reset);
            return null;
        }

        // Grouped listing: chat, then vision, then embedding
        var flat = new List<ModelCatalogEntry>();
        foreach (var group in new[] { CatalogModelCategory.Chat, CatalogModelCategory.Vision, CatalogModelCategory.Embedding })
        {
            var entries = selectable.Where(m => m.Category == group).ToList();
            if (entries.Count == 0) continue;
            _console.WriteLineColored(_color.Cyan + _color.Bold + GroupTitle(group) + _color.Reset);
            foreach (var m in entries)
            {
                flat.Add(m);
                var star = m.Recommended ? " ★" : "";
                _console.WriteLineColored(
                    $"  [{flat.Count}] {m.DisplayName}{star}  ({m.TotalSizeGb:0.##} GB) — {m.Notes}");
            }
            _console.BlankLine();
        }

        _console.WriteLineColored("Enter numbers to install (comma-separated, e.g. 1,3), 'a' for all ★ recommended, or Enter to skip:");
        var input = _console.PromptRaw("> ")?.Trim() ?? "";

        if (input.Length == 0 || _console.IsQuitRequested) return null;

        List<int> picks;
        if (input.Equals("a", StringComparison.OrdinalIgnoreCase))
            picks = flat.Select((m, i) => (m, i)).Where(t => t.m.Recommended).Select(t => t.i + 1).ToList();
        else
        {
            picks = new List<int>();
            foreach (var token in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(token, out var n) && n >= 1 && n <= flat.Count && !picks.Contains(n))
                    picks.Add(n);
            }
        }

        if (picks.Count == 0)
        {
            _console.WriteLineColored(_color.Yellow + "Nothing selected — skipping setup." + _color.Reset);
            return null;
        }

        string? chosenChat = null;
        foreach (var idx in picks)
        {
            var entry = flat[idx - 1];
            _console.BlankLine();
            _console.WriteLineColored(_color.Cyan + _color.Bold +
                $"▼ Downloading {entry.DisplayName} ({entry.TotalSizeGb:0.##} GB)" + _color.Reset);

            var lastPercent = -1;
            var result = await _installer.InstallAsync(entry, p =>
            {
                // Update at most once per percent to avoid flooding the terminal
                var pct = (int)p.Percent;
                if (pct == lastPercent) return;
                lastPercent = pct;
                var sizeInfo = p.TotalBytes.HasValue
                    ? $"{p.BytesReceived / 1048576.0:0} / {p.TotalBytes.Value / 1048576.0:0} MB"
                    : $"{p.BytesReceived / 1048576.0:0} MB";
                _console.WriteLineColored($"\r  [{new string('█', pct / 4)}{new string('░', 25 - pct / 4)}] {pct,3}%  {sizeInfo}  {p.MbPerSecond:0.#} MB/s");
            }, ct);

            if (result.Success)
            {
                _console.WriteLineColored(_color.Green + _color.Bold + $"✔ {result.Message}" + _color.Reset);
                if (entry.Category == CatalogModelCategory.Chat && chosenChat == null)
                    chosenChat = entry.Id;
            }
            else
            {
                _console.WriteLineColored(_color.Red + $"✘ {result.Message}" + _color.Reset);
            }
        }

        _console.BlankLine();
        return chosenChat;
    }

    private static string GroupTitle(CatalogModelCategory category) => category switch
    {
        CatalogModelCategory.Chat => "── Chat models ──",
        CatalogModelCategory.Vision => "── Vision models (image understanding) ──",
        CatalogModelCategory.Embedding => "── Embedding models (vector memory) ──",
        _ => "── Models ──"
    };
}
