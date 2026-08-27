using ECAssistant.Core;
using ECAssistant.Core.Config;
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
    private readonly RemoteProviderSetupWriter? _remoteWriter;
    private readonly VectorMemorySetupWriter? _vectorMemoryWriter;
    private bool _vectorMemoryEnabled = true;
    private bool _visionEnabled = true;
    private bool _useGpu = true;
    private bool _remoteConfigured;
    private string _remoteEndpoint = "";
    private string _remoteApiKey = "";
    private string _remoteModelId = "";

    public FirstRunWizard(
        IGuiConsole console,
        EColor color,
        ModelCatalogDocument catalog,
        ModelInstallerService installer,
        FirstRunStatus status,
        RemoteProviderSetupWriter? remoteWriter = null,
        VectorMemorySetupWriter? vectorMemoryWriter = null)
    {
        _console = console;
        _color = color;
        _catalog = catalog;
        _installer = installer;
        _status = status;
        _remoteWriter = remoteWriter;
        _vectorMemoryWriter = vectorMemoryWriter;
    }

    /// <summary>
    /// Run the wizard interactively. Returns the chosen chat entry id (or null if skipped).
    /// vision/embedding picks are additional and non-blocking.
    /// </summary>
    public async Task<string?> RunAsync(CancellationToken ct = default)
    {
        _console.WriteLineColored(_color.Yellow + _color.Bold +
            "════════ First-Run Setup — no models detected ════════" + _color.Reset);
        _console.BlankLine();

        // Mode choice: local GGUF models (catalog + downloads) or remote OpenAI-compatible API.
        if (!await TryRunRemoteSetup())
        {
            _console.WriteLineColored(_color.Yellow + "Remote setup skipped — falling back to local model setup." + _color.Reset);
            _console.BlankLine();
        }
        else
        {
            return null; // Remote configured — no downloads needed.
        }

        // Vector memory: explicit opt-in/opt-out, persisted to appsettings.json
        if (!TryPromptVectorMemory())
        {
            _console.BlankLine();
            return null; // Vector memory disabled — no embeddings model needed.
        }

        // Vision capability: decides which model groups are offered and whether
        // mmproj projectors are downloaded/wired alongside the models.
        var visionAnswer = _console.PromptRaw("Enable vision (image understanding)? [Y/n]: ")?.Trim().ToLowerInvariant() ?? "";
        _visionEnabled = visionAnswer != "n" && visionAnswer != "no";
        _console.BlankLine();

        // Hardware preference once — drives gpu_layers suggestions for everything below
        var gpuAnswer = _console.PromptRaw("Use GPU acceleration (Metal/CUDA)? [Y/n]: ")?.Trim().ToLowerInvariant() ?? "";
        _useGpu = gpuAnswer != "n" && gpuAnswer != "no";
        _console.BlankLine();

        var catalogPath = "model-catalog.json";
        _console.WriteLineColored(
            $"The model catalog is at {catalogPath} — edit it anytime to add your own models." +
            _color.Reset);
        _console.BlankLine();

        // Hint when model files already exist (e.g. after /reinstall, which keeps models)
        if (_status.InstalledFiles.Count > 0)
        {
            _console.WriteLineColored(_color.Green +
                $"✔ {_status.InstalledFiles.Count} model file(s) already exist in the models folder — " +
                "entries marked ✔ below need no download." + _color.Reset);
            _console.BlankLine();
        }

        // Show the full catalog; installed entries are marked ✔ and not selectable again
        var installedIds = _status.InstalledEntryIds;
        var selectable = _catalog.Models
            .Where(m => !installedIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (selectable.Count == 0)
        {
            _console.WriteLineColored(_color.Green + "All catalog models are already installed — nothing to download." + _color.Reset);
            return null;
        }

        // Grouped listing — driven by the vision choice:
        //   vision ON  → vision models + embeddings
        //   vision OFF → chat models + embeddings
        var flat = new List<ModelCatalogEntry>();
        var groups = _visionEnabled
            ? new[] { CatalogModelCategory.Vision, CatalogModelCategory.Embedding }
            : new[] { CatalogModelCategory.Chat, CatalogModelCategory.Embedding };
        foreach (var group in groups)
        {
            var entries = selectable.Where(m => m.Category == group).ToList();
            var installedEntries = _catalog.Models.Where(m => m.Category == group && installedIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            if (entries.Count == 0 && installedEntries.Count == 0) continue;
            _console.WriteLineColored(_color.Cyan + _color.Bold + GroupTitle(group) + _color.Reset);
            // Installed entries first — shown as ✔, not selectable
            foreach (var m in installedEntries)
            {
                _console.WriteLineColored(_color.Green + $"  ✔ {m.DisplayName}  (already installed)" + _color.Reset);
            }
            foreach (var m in entries)
            {
                flat.Add(m);
                var star = m.Recommended ? " ★" : "";
                _console.WriteLineColored(
                    $"  [{flat.Count}] {m.DisplayName}{star}  ({m.TotalSizeGb:0.##} GB, ~{m.TotalSizeGb * 1.15:0.#} GB memory) — {m.Notes}");
            }
            _console.BlankLine();
        }

        // Orphan GGUFs: files in the models folder that no catalog entry references —
        // shown as existing local models, selectable without download.
        // Vision pairing: a model file only counts as vision-capable when its matching
        // mmproj projector sits next to it; vision-capable orphans are only offered
        // when the user enabled vision (config must wire mmproj_path to be usable).
        var catalogFilenames = _catalog.Models
            .SelectMany(m => m.Files)
            .Select(f => f.Filename)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = _status.InstalledFiles
            .Where(f => !catalogFilenames.Contains(f))
            .Where(f => !ECAssistant.Core.Setup.ModelInstallerService.LooksLikeEmbeddingModel(f))
            .Where(f => _installer.DetectSiblingMmproj(f) != null == _visionEnabled)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (orphans.Count > 0)
        {
            _console.WriteLineColored(_color.Cyan + _color.Bold + "── Existing local models (found in models folder) ──" + _color.Reset);
            foreach (var f in orphans)
            {
                flat.Add(new ModelCatalogEntry
                {
                    Id = "local:" + f,
                    DisplayName = Path.GetFileNameWithoutExtension(f),
                    Category = CatalogModelCategory.Chat,
                    HfRepo = "local",
                    Notes = "Existing model file — no download needed."
                });
                _console.WriteLineColored($"  [{flat.Count}] {Path.GetFileNameWithoutExtension(f)}  ({f})");
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

        // Pre-flight: internet reachability + disk space for the selected downloads
        var pickedEntries = picks.Select(i => flat[i - 1]).Where(e => e.HfRepo != "local").ToList();
        if (pickedEntries.Count > 0)
        {
            if (!await ModelInstallerService.IsInternetAvailableAsync())
            {
                _console.WriteLineColored(_color.Red + "✘ No internet — huggingface.co is unreachable. Check your connection and run /reinstall to try again." + _color.Reset);
                return chosenChat;
            }

            var totalGb = pickedEntries.Where(e => !_installer.IsInstalled(e)).Sum(e => e.TotalSizeGb);
            var free = _installer.GetFreeSpaceGb();
            if (free.HasValue && totalGb > 0 && free.Value < totalGb + 2)
            {
                _console.WriteLineColored(_color.Red +
                    $"✘ Not enough disk space: {totalGb:0.#} GB needed (+2 GB margin), only {free:0.#} GB free." + _color.Reset);
                return chosenChat;
            }
        }

        // Apply the GPU preference to every entry we're about to install
        foreach (var e in pickedEntries)
            e.SuggestedConfig.GpuLayers = _useGpu ? 99 : 0;

        foreach (var idx in picks)
        {
            var entry = flat[idx - 1];

            // Orphan local file — register into config, no download
            if (entry.HfRepo == "local")
            {
                var filename = entry.Id["local:".Length..];
                var msg = _installer.RegisterLocalModelFile(filename);
                _console.WriteLineColored(_color.Green + $"✔ Registered local model '{filename}' — {msg}" + _color.Reset);
                chosenChat ??= entry.Id;
                continue;
            }

            _console.BlankLine();
            InstallResult result;
            var attempt = 0;
            while (true)
            {
                result = await DownloadEntryAsync(entry, ct);
                if (result.Success) break;
                attempt++;
                if (attempt >= 3) break;
                var retry = _console.PromptRaw($"Download failed — retry? [Y/n] (attempt {attempt}/3): ")?.Trim().ToLowerInvariant() ?? "";
                if (retry == "n" || retry == "no") break;
            }

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

        // Local mode needs an embeddings model for vector memory — make sure one exists
        if (chosenChat != null && _vectorMemoryEnabled)
            await EnsureEmbeddingsModelAsync(ct);

        // Prove the installation works: one tiny completion (remote live, local when server is up)
        await TryPostInstallTestAsync();

        // Optional cleanup: remove model files the user no longer wants
        TryRemoveModels();

        _console.BlankLine();
        return chosenChat;
    }

    /// <summary>Tiny completion through the fresh setup — remote tests live; local is checked when the server is up.</summary>
    private async Task TryPostInstallTestAsync()
    {
        try
        {
            if (_remoteConfigured)
            {
                _console.WriteLineColored(_color.Dim + "Testing remote setup with a tiny completion..." + _color.Reset);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                if (!string.IsNullOrEmpty(_remoteApiKey))
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _remoteApiKey);

                var body = System.Text.Json.JsonSerializer.Serialize(new
                {
                    model = _remoteModelId,
                    messages = new[] { new { role = "user", content = "Reply with exactly: OK" } },
                    max_tokens = 10
                });
                using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync($"{_remoteEndpoint.TrimEnd('/')}/chat/completions", content);

                if (resp.IsSuccessStatusCode)
                    _console.WriteLineColored(_color.Green + "✔ Remote test passed — the model answered." + _color.Reset);
                else
                    _console.WriteLineColored(_color.Red + $"✘ Remote test failed ({(int)resp.StatusCode}) — check endpoint/model/key, /reinstall to redo." + _color.Reset);
                return;
            }

            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var up = await probe.GetAsync("http://localhost:58777/eca/health");
            _console.WriteLineColored(_color.Dim + (up.IsSuccessStatusCode
                ? "Local LLM server is running — send a message to verify your model."
                : "Local server starts with the app — send a message to verify your model.") + _color.Reset);
        }
        catch
        {
            _console.WriteLineColored(_color.Dim + "(Post-install test skipped — verify by sending a message.)" + _color.Reset);
        }
    }

    /// <summary>Optional cleanup: list installed GGUFs and let the user delete some to free disk.</summary>
    private void TryRemoveModels()
    {
        var answer = _console.PromptRaw("Remove any installed model files? [y/N]: ")?.Trim().ToLowerInvariant() ?? "";
        if (answer != "y" && answer != "yes") return;

        var files = _installer.ListModelFiles();
        if (files.Count == 0)
        {
            _console.WriteLineColored(_color.Dim + "No model files installed." + _color.Reset);
            return;
        }

        for (var i = 0; i < files.Count; i++)
            _console.WriteLineColored($"  [{i + 1}] {files[i].Item1}  ({files[i].Item2:0.##} GB)");

        var input = _console.PromptRaw("Number(s) to delete (comma-separated, Enter to skip): ")?.Trim() ?? "";
        if (input.Length == 0 || _console.IsQuitRequested) return;

        foreach (var token in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(token, out var n) || n < 1 || n > files.Count) continue;
            var name = files[n - 1].Item1;
            _console.WriteLineColored(_installer.RemoveModelFile(name)
                ? _color.Green + $"✔ Deleted {name}" + _color.Reset + _color.Dim + " (llm-server.json may still reference it — /reinstall cleans that up)" + _color.Reset
                : _color.Red + $"✘ Could not delete {name}" + _color.Reset);
        }
    }

    /// <summary>
    /// Make sure a local embeddings model is present and wired into llm-server.json:
    /// 1. already-installed embedding catalog entry → wire it,
    /// 2. embed-looking GGUF in the models folder → register it,
    /// 3. otherwise offer to download the recommended embedding model (tiny).
    /// </summary>
    private async Task EnsureEmbeddingsModelAsync(CancellationToken ct)
    {
        var embedEntries = _catalog.Models
            .Where(m => m.Category == CatalogModelCategory.Embedding)
            .ToList();

        // 1. Catalog embedding entry already on disk → just wire it
        var installed = embedEntries.FirstOrDefault(e => _installer.IsInstalled(e));
        if (installed != null)
        {
            var msg = _installer.ApplyToServerConfig(installed);
            _console.WriteLineColored(_color.Green + $"✔ Embeddings wired: {installed.DisplayName} — {msg}" + _color.Reset);
            return;
        }

        // 2. Embedding-looking GGUF in the models folder that no catalog entry claims
        var catalogFilenames = _catalog.Models.SelectMany(m => m.Files).Select(f => f.Filename).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localEmbed = _status.InstalledFiles
            .FirstOrDefault(f => !catalogFilenames.Contains(f) && ModelInstallerService.LooksLikeEmbeddingModel(f));
        if (localEmbed != null)
        {
            var msg = _installer.RegisterLocalModelFile(localEmbed, isEmbedding: true);
            _console.WriteLineColored(_color.Green + $"✔ Embeddings wired from existing file '{localEmbed}' — {msg}" + _color.Reset);
            return;
        }

        // 3. Nothing available — offer the smallest recommended embedding model
        var suggestion = embedEntries.OrderBy(m => m.TotalSizeGb).FirstOrDefault();
        if (suggestion == null) return; // catalog has no embedding models

        _console.WriteLineColored(_color.Yellow +
            $"⚠ No embeddings model found — vector memory / KB search needs one." + _color.Reset);
        var answer = _console.PromptRaw("Download " + suggestion.DisplayName + $" ({suggestion.TotalSizeGb:0.##} GB)? [Y/n]: ")?.Trim().ToLowerInvariant();
        if (answer == "n" || answer == "no")
        {
            _console.WriteLineColored(_color.Dim + "Skipped — vector memory will be unavailable until an embeddings model is configured." + _color.Reset);
            return;
        }

        _console.BlankLine();
        var result = await DownloadEntryAsync(suggestion, ct);
        _console.WriteLineColored(result.Success
            ? _color.Green + $"✔ Embeddings ready: {suggestion.DisplayName}" + _color.Reset
            : _color.Red + $"✘ Embeddings download failed: {result.Message}" + _color.Reset);
    }

    /// <summary>Download (or reuse) a catalog entry with progress display and wire it into llm-server.json.</summary>
    private async Task<InstallResult> DownloadEntryAsync(ModelCatalogEntry entry, CancellationToken ct)
    {
        _console.WriteLineColored(_color.Cyan + _color.Bold +
            $"▼ Downloading {entry.DisplayName} ({entry.TotalSizeGb:0.##} GB)" + _color.Reset);

        var lastPercent = -1;
        return await _installer.InstallAsync(entry, p =>
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
    }

    /// <summary>
    /// Ask whether vector memory (semantic search) should be activated.
    /// Persists the choice; returns true when activated.
    /// </summary>
    private bool TryPromptVectorMemory()
    {
        _console.WriteLineColored("Activate vector memory (semantic search over your memories/files)?");
        _console.WriteLineColored(_color.Dim + "  Needs an embeddings model — local installs wire one automatically, remote uses the provider's embedding model." + _color.Reset);
        var answer = _console.PromptRaw("Activate? [Y/n]: ")?.Trim().ToLowerInvariant() ?? "";
        _vectorMemoryEnabled = answer != "n" && answer != "no";

        try { _vectorMemoryWriter?.SetEnabled(_vectorMemoryEnabled); }
        catch (Exception ex)
        {
            _console.WriteLineColored(_color.Yellow + $"Could not persist vector memory setting: {ex.Message}" + _color.Reset);
        }

        if (!_vectorMemoryEnabled)
            _console.WriteLineColored(_color.Dim + "Vector memory disabled — re-enable in appsettings.json or via /reinstall." + _color.Reset);

        return _vectorMemoryEnabled;
    }

    /// <summary>
    /// Prompt for remote (OpenAI-compatible) API settings and persist them.
    /// Returns true when remote mode was configured; false when the user skipped
    /// or the writer is unavailable (then local catalog flow should run).
    /// </summary>
    private async Task<bool> TryRunRemoteSetup()
    {
        _console.WriteLineColored("How should ECAssistant run its AI?");
        _console.WriteLineColored(_color.Cyan + "  [1] Local models  (GGUF on this machine — free, private, downloaded below)" + _color.Reset);
        _console.WriteLineColored(_color.Cyan + "  [2] Remote AI     (OpenAI-compatible API: OpenAI, OpenRouter, Ollama cloud, …)" + _color.Reset);
        var choice = _console.PromptRaw("Choose [1/2, Enter = 1]: ")?.Trim() ?? "";

        if (choice != "2") return false;
        if (_console.IsQuitRequested) return false;

        if (_remoteWriter == null)
        {
            _console.WriteLineColored(_color.Yellow + "Remote setup is not available in this context." + _color.Reset);
            return false;
        }

        _console.BlankLine();
        _console.WriteLineColored(_color.Cyan + _color.Bold + "▼ Remote AI setup" + _color.Reset);

        var endpoint = _console.PromptRaw("  Endpoint (e.g. https://api.openai.com/v1): ")?.Trim() ?? "";
        if (endpoint.Length == 0) return false;

        var apiKey = _console.PromptRaw("  API key: ")?.Trim() ?? "";

        var modelId = _console.PromptRaw("  Model ID (e.g. gpt-4o-mini): ")?.Trim() ?? "";
        if (modelId.Length == 0) return false;

        var embeddingModelId = _console.PromptRaw("  Embedding model ID [Enter = text-embedding-3-small]: ")?.Trim() ?? "";
        if (embeddingModelId.Length == 0) embeddingModelId = "text-embedding-3-small";

        // Verify the endpoint actually works before saving anything
        _console.WriteLineColored(_color.Dim + "  Testing connection..." + _color.Reset);
        var reachable = await TestRemoteConnectionAsync(endpoint, apiKey);
        if (!reachable)
        {
            var saveAnyway = _console.PromptRaw("  Connection failed — save settings anyway? [y/N]: ")?.Trim().ToLowerInvariant() ?? "";
            if (saveAnyway != "y" && saveAnyway != "yes") return false;
        }
        else
        {
            _console.WriteLineColored(_color.Green + "  ✔ Endpoint reachable." + _color.Reset);
        }

        var name = new UriBuilder(endpoint).Host; // e.g. api.openai.com
        try
        {
            _remoteWriter.Write(new RemoteProviderConfig
            {
                Name = name,
                Endpoint = endpoint,
                ApiKey = apiKey.Length > 0 ? apiKey : null,
                ModelId = modelId,
                EmbeddingModelId = embeddingModelId
            });
        }
        catch (Exception ex)
        {
            _console.WriteLineColored(_color.Red + $"✘ Could not save remote settings: {ex.Message}" + _color.Reset);
            return false;
        }

        _console.WriteLineColored(_color.Green + _color.Bold +
            $"✔ Remote AI configured: {modelId} @ {endpoint} (key encrypted to key store)" + _color.Reset);
        _remoteConfigured = true;
        _remoteEndpoint = endpoint;
        _remoteApiKey = apiKey;
        _remoteModelId = modelId;
        return true;
    }

    /// <summary>GET {endpoint}/models with the API key; true when the endpoint answers.</summary>
    private static async Task<bool> TestRemoteConnectionAsync(string endpoint, string apiKey)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrEmpty(apiKey))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await http.GetAsync($"{endpoint.TrimEnd('/')}/models");
            // 401/403 = reachable but key wrong — still a config problem
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string GroupTitle(CatalogModelCategory category) => category switch
    {
        CatalogModelCategory.Chat => "── Chat models ──",
        CatalogModelCategory.Vision => "── Vision models (image understanding) ──",
        CatalogModelCategory.Embedding => "── Embedding models (vector memory) ──",
        _ => "── Models ──"
    };
}
