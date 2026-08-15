using ECAssistant.Config;
using ECAssistant.Interfaces;
using Moq;

namespace ECAssistant.Tests.Config;

public class ConfigLoaderTests
{
    private readonly Mock<IFileSystem> _mockFileSystem;

    public ConfigLoaderTests()
    {
        _mockFileSystem = new Mock<IFileSystem>();
    }

    [Fact]
    public void Constructor_WithValidFileSystem_CreatesInstance()
    {
        var loader = new ConfigLoader(_mockFileSystem.Object);
        Assert.NotNull(loader);
    }

    [Fact]
    public void Constructor_WithFileSystemAndColor_CreatesInstance()
    {
        var loader = new ConfigLoader(_mockFileSystem.Object);
        Assert.NotNull(loader);
    }

    [Fact]
    public void Constructor_WithNullFileSystem_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigLoader(null!));
    }

    [Fact]
    public void Constructor_WithNullColorFormatter_CreatesInstance()
    {
        var loader = new ConfigLoader(_mockFileSystem.Object);
        Assert.NotNull(loader);
    }

    [Fact]
    public void Load_ValidJson_ReturnsConfig()
    {
        var json = """{"root_path":"MyProject","memory":{"data_path":"MyMemory"}}""";
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.ReadFile(It.IsAny<string>())).Returns(json);

        var loader = new ConfigLoader(_mockFileSystem.Object);
        var config = loader.Load("appsettings.json");

        Assert.NotNull(config);
        Assert.Equal("MyProject", config.RootPath);
        Assert.Equal("MyMemory", config.Memory.DataPath);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaultConfig()
    {
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(false);

        var loader = new ConfigLoader(_mockFileSystem.Object);
        var config = loader.Load("nonexistent.json");

        Assert.NotNull(config);
        Assert.Equal(".", config.RootPath);
    }

    [Fact]
    public void Load_InvalidJson_ReturnsDefaultConfig()
    {
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.ReadFile(It.IsAny<string>())).Returns("not valid json {{{");

        var loader = new ConfigLoader(_mockFileSystem.Object);
        var config = loader.Load("appsettings.json");

        Assert.NotNull(config);
        Assert.Equal(".", config.RootPath);
    }

    [Fact]
    public void Load_EmptyJson_ReturnsDefaultConfig()
    {
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.ReadFile(It.IsAny<string>())).Returns("");

        var loader = new ConfigLoader(_mockFileSystem.Object);
        var config = loader.Load("appsettings.json");

        Assert.NotNull(config);
        Assert.Equal(".", config.RootPath);
    }

    [Fact]
    public void Load_NullJson_ReturnsDefaultConfig()
    {
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.ReadFile(It.IsAny<string>())).Returns((string)null!);

        var loader = new ConfigLoader(_mockFileSystem.Object);
        var config = loader.Load("appsettings.json");

        Assert.NotNull(config);
    }

    [Fact]
    public void Load_FileExistsButDeserializesToNull_ReturnsDefaultConfig()
    {
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.ReadFile(It.IsAny<string>())).Returns("null");

        var loader = new ConfigLoader(_mockFileSystem.Object);
        var config = loader.Load("appsettings.json");

        Assert.NotNull(config);
        Assert.Equal(".", config.RootPath);
    }

    [Fact]
    public void Load_DefaultFilePath_UsesAppsettingsJson()
    {
        _mockFileSystem.Setup(fs => fs.FileExists("appsettings.json")).Returns(false);

        var loader = new ConfigLoader(_mockFileSystem.Object);
        loader.Load();

        _mockFileSystem.Verify(fs => fs.FileExists("appsettings.json"), Times.Once);
    }

    [Fact]
    public void Load_CustomFilePath_UsesSpecifiedPath()
    {
        _mockFileSystem.Setup(fs => fs.FileExists("custom.json")).Returns(false);

        var loader = new ConfigLoader(_mockFileSystem.Object);
        loader.Load("custom.json");

        _mockFileSystem.Verify(fs => fs.FileExists("custom.json"), Times.Once);
    }

    [Fact]
    public void Load_ReadFileThrows_ReturnsDefaultConfig()
    {
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.ReadFile(It.IsAny<string>())).Throws(new IOException("disk error"));

        var loader = new ConfigLoader(_mockFileSystem.Object);
        var config = loader.Load("appsettings.json");

        Assert.NotNull(config);
        Assert.Equal(".", config.RootPath);
    }
}