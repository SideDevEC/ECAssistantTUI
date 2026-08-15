using System.Text;
using System.Text.Json;
using ECAssistant.Interfaces;
using ECAssistant.Services;

namespace ECAssistant.Tools.Background;

/// <summary>
/// Background Exec Tool — lets the LLM start long-running processes
/// without blocking the agent loop.
///
/// The LLM can:
///   - Start a build in background: EBackgroundExec(action="start", command="dotnet build")
///   - Check status: EBackgroundExec(action="status")
///   - Get output: EBackgroundExec(action="output", id="bg-1")
///   - Kill: EBackgroundExec(action="kill", id="bg-1")
/// </summary>
public class EBackgroundExecTool : ITool
{
    private readonly BackgroundProcessManager _mgr;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fileSystem;
    private readonly IConfigProvider _configProvider;
    private readonly IColorFormatter _colorFormatter;

    private string WorkingDir => _configProvider.GetValue("background.workingDir", Environment.CurrentDirectory);

    public EBackgroundExecTool(
        BackgroundProcessManager mgr,
        IProcessRunner processRunner,
        IFileSystem fileSystem,
        IConfigProvider configProvider,
        IColorFormatter colorFormatter)
    {
        _mgr = mgr;
        _processRunner = processRunner;
        _fileSystem = fileSystem;
        _configProvider = configProvider;
        _colorFormatter = colorFormatter;
    }

    public string Name => "EBackgroundExec";

    public string Description =>
        "Start, check, or kill background processes. Non-blocking — lets you run long commands " +
        "like builds while continuing to work. Use action=start to begin, action=status to list, " +
        "action=output to get results, action=kill to terminate.";

    public Interfaces.ToolPolicy GetPolicy() => Interfaces.ToolPolicy.Allowed(Name);

    public async Task<string> ExecuteAsync(string input, CancellationToken ct = default)
    {
        var args = ParseInput(input);
        var action = args.GetValueOrDefault("action")?.ToLower().Trim();

        switch (action)
        {
            case "start":
            {
                var command = args.GetValueOrDefault("command");
                if (string.IsNullOrWhiteSpace(command))
                    return $"[{Name}] ERROR: Missing 'command' argument for action=start.";

                if (ct.IsCancellationRequested)
                    return $"[{Name}] ERROR: [CANCELLED] Background process start was cancelled by user.";

                var id = await _mgr.StartAsync(command, WorkingDir);
                return $"[{Name}] Background process started: {id}\nCommand: {command}\nUse EBackgroundExec with action=output and id={id} to check results.";
            }

            case "status":
            {
                var list = _mgr.List();
                if (list.Count == 0)
                    return $"[{Name}] No background processes running.";

                var sb = new StringBuilder();
                sb.AppendLine($"Background processes ({list.Count}):");
                foreach (var p in list)
                    sb.AppendLine($"  {p}");
                return $"[{Name}] {sb}";
            }

            case "output":
            {
                var id = args.GetValueOrDefault("id");
                if (string.IsNullOrWhiteSpace(id))
                    return $"[{Name}] ERROR: Missing 'id' argument for action=output.";

                var status = _mgr.GetStatus(id);
                var output = _mgr.GetOutput(id);
                return $"[{Name}] Process {id} — Status: {status}\n\n{output}";
            }

            case "kill":
            {
                var id = args.GetValueOrDefault("id");
                if (string.IsNullOrWhiteSpace(id))
                    return $"[{Name}] ERROR: Missing 'id' argument for action=kill.";

                var killed = _mgr.Kill(id);
                return killed
                    ? $"[{Name}] Killed process: {id}"
                    : $"[{Name}] ERROR: Failed to kill process: {id} (not running or not found)";
            }

            default:
                return $"[{Name}] ERROR: Unknown action: '{action}'. Use start, status, output, or kill.";
        }
    }

    private Dictionary<string, string> ParseInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new Dictionary<string, string>();

        try
        {
            using var doc = JsonDocument.Parse(input);
            var dict = new Dictionary<string, string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
                else
                    dict[prop.Name] = prop.Value.ToString();
            }
            return dict;
        }
        catch
        {
            var dict = new Dictionary<string, string>();
            var pairs = input.Split('&');
            foreach (var pair in pairs)
            {
                var eq = pair.IndexOf('=');
                if (eq > 0)
                    dict[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
            }
            return dict;
        }
    }
}