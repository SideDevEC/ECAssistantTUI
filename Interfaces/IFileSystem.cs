using System.Collections.Generic;

namespace ECAssistant.Interfaces;

/// <summary>
/// File system abstraction.
/// </summary>
public interface IFileSystem
{
    string ReadFile(string path);
    string[] ListFiles(string directory, string pattern = "*");
    bool FileExists(string path);
    void WriteFile(string path, string content);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
}