using ECAssistant.Core;
using ECAssistant.Core.Composition;
using ECAssistant.Core.Setup;
using ECAssistant.TUI.Controller;
using ECAssistant.TUI.UI;

namespace ECAssistant.TUI.Hosting;

/// <summary>
/// Host facade for thin console hosts: the ONLY entry point a host needs to run
/// first-run setup, validate configuration, and build+run the chat app. Hides all
/// Core types so hosts can depend on the TUI alone — the dependency chain stays
/// strictly Console → TUI → Core.
/// </summary>
public static class TuiAppHost
{
    /// <summary>Run first-run/reinstall setup when needed (uses the TUI's own setup UI).</summary>
    public static async Task RunFirstRunSetupAsync(string userConfigDir)
    {
        var orchestrator = new FirstRunOrchestrator(userConfigDir, new TuiSetupUi(new EGuiConsole()));
        await orchestrator.RunIfNeededAsync().ConfigureAwait(false);
    }

    /// <summary>True when appsettings.json configures a remote provider.</summary>
    public static bool IsRemoteModeConfigured(string userConfigDir)
    {
        var appsettingsPath = Path.Combine(userConfigDir, "appsettings.json");
        try
        {
            return File.Exists(appsettingsPath) && FirstRunOrchestrator.IsRemoteProviderConfigured(appsettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false; // unreadable config → treat as not configured; startup falls back to local checks
        }
    }

    /// <summary>True when a local model is installed and wired in llm-server.json.</summary>
    public static bool IsLocalModelUsable(string userConfigDir)
        => FirstRunOrchestrator.IsLocalModelUsable(
            Path.Combine(userConfigDir, "appsettings.json"),
            Path.Combine(LlmRoot(), "llm-server.json"));

    /// <summary>Shared LLM root (~/.ECAssistantLLM).</summary>
    public static string LlmRoot()
        => PathExpander.Default.Expand("~/.ECAssistantLLM");

    /// <summary>
    /// Build the service bundle and hand back a ready-to-run AppController.
    /// Throws InvalidOperationException with a user-facing message when the
    /// configuration has no usable model/provider.
    /// </summary>
    public static AppController CreateApp(string userConfigDir, string[] args)
    {
        var root = new EcaCompositionRoot(userConfigDir, args);
        var services = root.Build();

        return new AppController(
            new EGuiConsole(),
            services.Config,
            services.ModelPath,
            services.WorkingDirectory,
            services.UserConfigDirectory,
            services.Logger,
            null, // externalTools: hosts have no plugin loading yet; AppController falls back to an empty tool set
            services.BackgroundProcesses,
            services.FileWatcher,
            new AiSetupResetter());
    }
}
