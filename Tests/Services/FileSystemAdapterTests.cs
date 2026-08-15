using ECAssistant.Services;
using ECAssistant.Interfaces;

namespace ECAssistant.Tests.Services;

public class FileSystemAdapterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemAdapter _adapter;

    public FileSystemAdapterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAssistantTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _adapter = new FileSystemAdapter();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Constructor_Default_CreatesInstance()
    {
        Assert.NotNull(_adapter);
    }

    [Fact]
    public void WriteFile_ValidPath_WritesContent()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        _adapter.WriteFile(path, "hello world");
        Assert.Equal("hello world", File.ReadAllText(path));
    }

    [Fact]
    public void ReadFile_ValidPath_ReturnsContent()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(path, "test content");
        Assert.Equal("test content", _adapter.ReadFile(path));
    }

    [Fact]
    public void ReadFile_NonExistentFile_ThrowsFileNotFoundException()
    {
        var path = Path.Combine(_tempDir, "nonexistent.txt");
        Assert.Throws<FileNotFoundException>(() => _adapter.ReadFile(path));
    }

    [Fact]
    public void FileExists_ExistingFile_ReturnsTrue()
    {
        var path = Path.Combine(_tempDir, "exists.txt");
        File.WriteAllText(path, "content");
        Assert.True(_adapter.FileExists(path));
    }

    [Fact]
    public void FileExists_NonExistentFile_ReturnsFalse()
    {
        Assert.False(_adapter.FileExists(Path.Combine(_tempDir, "nope.txt")));
    }

    [Fact]
    public void DirectoryExists_ExistingDirectory_ReturnsTrue()
    {
        Assert.True(_adapter.DirectoryExists(_tempDir));
    }

    [Fact]
    public void DirectoryExists_NonExistentDirectory_ReturnsFalse()
    {
        Assert.False(_adapter.DirectoryExists(Path.Combine(_tempDir, "nonexistent_dir")));
    }

    [Fact]
    public void CreateDirectory_NewDirectory_CreatesDirectory()
    {
        var path = Path.Combine(_tempDir, "newdir");
        _adapter.CreateDirectory(path);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void CreateDirectory_ExistingDirectory_DoesNotThrow()
    {
        _adapter.CreateDirectory(_tempDir);
        Assert.True(Directory.Exists(_tempDir));
    }

    [Fact]
    public void ListFiles_DirectoryWithFiles_ReturnsAllFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_tempDir, "b.txt"), "b");
        var files = _adapter.ListFiles(_tempDir);
        Assert.True(files.Length >= 2);
    }

    [Fact]
    public void ListFiles_WithPattern_FiltersCorrectly()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_tempDir, "b.log"), "b");
        var files = _adapter.ListFiles(_tempDir, "*.txt");
        Assert.Single(files);
        Assert.EndsWith("a.txt", files[0]);
    }

    [Fact]
    public void ListFiles_EmptyDirectory_ReturnsEmptyArray()
    {
        var files = _adapter.ListFiles(_tempDir);
        Assert.Empty(files);
    }

    [Fact]
    public void WriteFile_OverwriteExistingFile_ReplacesContent()
    {
        var path = Path.Combine(_tempDir, "overwrite.txt");
        _adapter.WriteFile(path, "original");
        _adapter.WriteFile(path, "replaced");
        Assert.Equal("replaced", File.ReadAllText(path));
    }

    [Fact]
    public void WriteFile_EmptyContent_WritesEmptyFile()
    {
        var path = Path.Combine(_tempDir, "empty.txt");
        _adapter.WriteFile(path, "");
        Assert.Equal("", File.ReadAllText(path));
    }

    [Fact]
    public void ReadFile_EmptyFile_ReturnsEmptyString()
    {
        var path = Path.Combine(_tempDir, "empty.txt");
        File.WriteAllText(path, "");
        Assert.Equal("", _adapter.ReadFile(path));
    }
}