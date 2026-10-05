namespace HomeBlaze.Services.Tests;

public class DataDirectorySeederTests : IDisposable
{
    private readonly DirectoryInfo _seedDirectory = Directory.CreateTempSubdirectory("homeblaze-seed-");
    private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("homeblaze-data-");

    public DataDirectorySeederTests()
    {
        File.WriteAllText(Path.Combine(_seedDirectory.FullName, "Root.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_seedDirectory.FullName, "Files", "Devices"));
        File.WriteAllText(Path.Combine(_seedDirectory.FullName, "Files", "Plugins.json"), "seed-plugins");
        File.WriteAllText(Path.Combine(_seedDirectory.FullName, "Files", "Devices", "Device.json"), "seed-device");
    }

    private string RootFile => Path.Combine(_dataDirectory.FullName, "Root.json");

    [Fact]
    public void WhenRootFileIsMissingAndSeedIsSet_ThenSeedIsCopied()
    {
        // Act
        var copiedFileCount = DataDirectorySeeder.SeedIfMissing(RootFile, _seedDirectory.FullName);

        // Assert
        Assert.Equal(3, copiedFileCount);
        Assert.True(File.Exists(RootFile));
        Assert.Equal("seed-plugins", File.ReadAllText(Path.Combine(_dataDirectory.FullName, "Files", "Plugins.json")));
        Assert.Equal("seed-device", File.ReadAllText(Path.Combine(_dataDirectory.FullName, "Files", "Devices", "Device.json")));
    }

    [Fact]
    public void WhenRootFileExists_ThenNothingIsCopied()
    {
        // Arrange
        File.WriteAllText(RootFile, "existing");

        // Act
        var copiedFileCount = DataDirectorySeeder.SeedIfMissing(RootFile, _seedDirectory.FullName);

        // Assert
        Assert.Equal(0, copiedFileCount);
        Assert.Equal("existing", File.ReadAllText(RootFile));
        Assert.False(Directory.Exists(Path.Combine(_dataDirectory.FullName, "Files")));
    }

    [Fact]
    public void WhenSeedDirectoryIsNotSet_ThenNothingIsCopied()
    {
        // Act
        var copiedFileCount = DataDirectorySeeder.SeedIfMissing(RootFile, null);

        // Assert
        Assert.Equal(0, copiedFileCount);
        Assert.False(File.Exists(RootFile));
    }

    [Fact]
    public void WhenSeedDirectoryDoesNotExist_ThenNothingIsCopied()
    {
        // Act
        var copiedFileCount = DataDirectorySeeder.SeedIfMissing(RootFile, Path.Combine(_seedDirectory.FullName, "missing"));

        // Assert
        Assert.Equal(0, copiedFileCount);
        Assert.False(File.Exists(RootFile));
    }

    [Fact]
    public void WhenConfiguredRootFileNameDiffersFromSeed_ThenSeedRootIsCopiedUnderConfiguredName()
    {
        // Arrange
        var configuredRootFile = Path.Combine(_dataDirectory.FullName, "Home.json");

        // Act
        var copiedFileCount = DataDirectorySeeder.SeedIfMissing(configuredRootFile, _seedDirectory.FullName);

        // Assert
        Assert.Equal(3, copiedFileCount);
        Assert.True(File.Exists(configuredRootFile));
        Assert.Equal("{}", File.ReadAllText(configuredRootFile));
        Assert.False(File.Exists(RootFile));
        Assert.Equal("seed-plugins", File.ReadAllText(Path.Combine(_dataDirectory.FullName, "Files", "Plugins.json")));
        Assert.Equal("seed-device", File.ReadAllText(Path.Combine(_dataDirectory.FullName, "Files", "Devices", "Device.json")));
    }

    [Fact]
    public void WhenDataDirectoryHasFilesButNoRootFile_ThenExistingFilesAreKept()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_dataDirectory.FullName, "Files"));
        File.WriteAllText(Path.Combine(_dataDirectory.FullName, "Files", "Plugins.json"), "user-plugins");

        // Act
        var copiedFileCount = DataDirectorySeeder.SeedIfMissing(RootFile, _seedDirectory.FullName);

        // Assert
        Assert.Equal(2, copiedFileCount);
        Assert.Equal("user-plugins", File.ReadAllText(Path.Combine(_dataDirectory.FullName, "Files", "Plugins.json")));
        Assert.True(File.Exists(RootFile));
    }

    public void Dispose()
    {
        _seedDirectory.Delete(recursive: true);
        _dataDirectory.Delete(recursive: true);
    }
}
