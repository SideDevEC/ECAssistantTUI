namespace ECAssistant.Interfaces;

/// <summary>
/// Configuration access abstraction.
/// </summary>
public interface IConfigProvider
{
    T GetSection<T>(string section) where T : class, new();
    string GetValue(string key, string defaultValue = "");
    int GetInt(string key, int defaultValue = 0);
    float GetFloat(string key, float defaultValue = 0f);
    bool GetBool(string key, bool defaultValue = false);
}