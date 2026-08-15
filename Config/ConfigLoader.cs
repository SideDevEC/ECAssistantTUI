using System;
using System.IO;
using System.Text.Json;
using ECAssistant.Interfaces;

namespace ECAssistant.Config;

/// <summary>
/// Loads EAgentConfig from JSON files.
/// Replaces the static EAgentConfig.Load() factory method.
/// </summary>
public class ConfigLoader
{
    private readonly IFileSystem _fileSystem;

    public ConfigLoader(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public EAgentConfig Load(string filePath = "appsettings.json")
    {
        try
        {
            if (_fileSystem.FileExists(filePath))
            {
                var json = _fileSystem.ReadFile(filePath);
                var config = JsonSerializer.Deserialize<EAgentConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (config != null) return config;
            }
        }
        catch (Exception ex)
        {
            // Silently fall back to defaults — callers handle null/missing config
        }
        return new EAgentConfig();
    }
}