using Namotion.NuGet.Plugins.Loading;
using Xunit;

namespace Namotion.NuGet.Plugins.Tests.Loading;

public class NuspecHelperTests
{
    [Fact]
    public void WhenNuspecExists_ThenReturnsMetadataAndXDocument()
    {
        // Arrange
        var directory = CreateExtractedPackageWithNuspec("TestPackage", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>TestPackage</id>
                <version>1.0.0</version>
                <title>Test Package Title</title>
                <description>A test package</description>
                <authors>Test Author</authors>
                <tags>sensor iot</tags>
                <projectUrl>https://example.com</projectUrl>
                <licenseUrl>https://example.com/license</licenseUrl>
                <iconUrl>https://example.com/icon.png</iconUrl>
              </metadata>
            </package>
            """);

        try
        {
            // Act
            var (metadata, nuspec) = NuspecHelper.ReadFromExtractedPackage(directory);

            // Assert
            Assert.NotNull(metadata);
            Assert.Equal("Test Package Title", metadata.Title);
            Assert.Equal("A test package", metadata.Description);
            Assert.Equal("Test Author", metadata.Authors);
            Assert.Equal(["sensor", "iot"], metadata.Tags);
            Assert.NotNull(metadata.ProjectUrl);
            Assert.Contains("example.com", metadata.ProjectUrl);
            Assert.NotNull(metadata.LicenseUrl);
            Assert.Contains("example.com/license", metadata.LicenseUrl);
            Assert.NotNull(metadata.IconUrl);
            Assert.Contains("example.com/icon.png", metadata.IconUrl);
            Assert.NotNull(nuspec);
        }
        finally
        {
            // Cleanup
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void WhenNoNuspecExists_ThenReturnsDefaults()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            // Act
            var (metadata, nuspec) = NuspecHelper.ReadFromExtractedPackage(directory);

            // Assert
            Assert.Equal("", metadata.Title);
            Assert.Equal("", metadata.Description);
            Assert.Equal("", metadata.Authors);
            Assert.Null(metadata.IconUrl);
            Assert.Null(metadata.ProjectUrl);
            Assert.Null(metadata.LicenseUrl);
            Assert.Empty(metadata.Tags);
            Assert.Null(nuspec);
        }
        finally
        {
            // Cleanup
            Directory.Delete(directory, true);
        }
    }

    private static string CreateExtractedPackageWithNuspec(string packageId, string nuspecXml)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{packageId}.nuspec"), nuspecXml);
        return directory;
    }
}
