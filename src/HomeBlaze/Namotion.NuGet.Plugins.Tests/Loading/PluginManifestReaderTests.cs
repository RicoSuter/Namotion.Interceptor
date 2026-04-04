using System.Text.Json;
using Namotion.NuGet.Plugins.Loading;
using Xunit;

namespace Namotion.NuGet.Plugins.Tests.Loading;

public class PluginManifestReaderTests
{
    [Fact]
    public void WhenPluginJsonExists_ThenReturnsManifest()
    {
        // Arrange
        var directory = CreateExtractedPackageWithPluginJson("""
            { "schema": 1, "hostDependencies": ["MyCompany.Abstractions"] }
            """);

        // Act
        var manifest = PluginManifestReader.Read(directory);

        // Assert
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest.Value.GetProperty("schema").GetInt32());
    }

    [Fact]
    public void WhenPluginJsonMissing_ThenReturnsNull()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        // Act
        var manifest = PluginManifestReader.Read(directory);

        // Assert
        Assert.Null(manifest);

        // Cleanup
        Directory.Delete(directory, true);
    }

    [Fact]
    public void WhenPluginJsonHasHostDependencies_ThenGetHostDependenciesReturnsThem()
    {
        // Arrange
        var directory = CreateExtractedPackageWithPluginJson("""
            { "schema": 1, "hostDependencies": ["Pkg.A", "Pkg.B"] }
            """);

        // Act
        var manifest = PluginManifestReader.Read(directory);
        var hostDependencies = PluginManifestReader.GetHostDependencies(manifest);

        // Assert
        Assert.Equal(["Pkg.A", "Pkg.B"], hostDependencies);
    }

    [Fact]
    public void WhenPluginJsonHasNoHostDependencies_ThenGetHostDependenciesReturnsEmpty()
    {
        // Arrange
        var directory = CreateExtractedPackageWithPluginJson("""
            { "schema": 1, "customField": "value" }
            """);

        // Act
        var manifest = PluginManifestReader.Read(directory);
        var hostDependencies = PluginManifestReader.GetHostDependencies(manifest);

        // Assert
        Assert.Empty(hostDependencies);
    }

    [Fact]
    public void WhenPluginJsonIsMalformed_ThenReturnsNull()
    {
        // Arrange
        var directory = CreateExtractedPackageWithPluginJson("not valid json {{{");

        // Act
        var manifest = PluginManifestReader.Read(directory);

        // Assert
        Assert.Null(manifest);
    }

    private static string CreateExtractedPackageWithPluginJson(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), json);
        return directory;
    }
}
