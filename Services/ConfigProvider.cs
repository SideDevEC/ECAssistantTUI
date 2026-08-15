using System;
using System.IO;
using System.Text.Json;
using ECAssistant.Interfaces;

namespace ECAssistant.Services;

/// <summary>
/// JSON configuration loader.
/// </summary>
public class ConfigProvider : IConfigProvider
{
    private readonly IFileSystem _fileSystem;
    private readonly string _configPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ConfigProvider(IFileSystem fileSystem, string configPath)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
    }

    private string LoadRawConfig()
    {
        return _fileSystem.ReadFile(_configPath);
    }

    public T GetSection<T>(string section) where T : class, new()
    {
        var raw = LoadRawConfig();
        var root = JsonDocument.Parse(raw);
        var sectionElement = root.RootElement.GetProperty(section);
        return JsonSerializer.Deserialize<T>(sectionElement.GetRawText(), _jsonOptions) ?? new T();
    }

    public string GetValue(string key, string defaultValue = "")
    {
        var raw = LoadRawConfig();
        var root = JsonDocument.Parse(raw);
        return root.RootElement.TryGetProperty(key, out var prop) ? prop.GetString() ?? defaultValue : defaultValue;
    }

    public int GetInt(string key, int defaultValue = 0)
    {
        var raw = LoadRawConfig();
        var root = JsonDocument.Parse(raw);
        return root.RootElement.TryGetProperty(key, out var prop) ? prop.GetInt32() : defaultValue;
    }

    public float GetFloat(string key, float defaultValue = 0f)
    {
        var raw = LoadRawConfig();
        var root = JsonDocument.Parse(raw);
        return root.RootElement.TryGetProperty(key, out var prop) ? prop.GetSingle() : defaultValue;
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        var raw = LoadRawConfig();
        var root = JsonDocument.Parse(raw);
        return root.RootElement.TryGetProperty(key, out var prop) ? prop.GetBoolean() : defaultValue;
    }
}