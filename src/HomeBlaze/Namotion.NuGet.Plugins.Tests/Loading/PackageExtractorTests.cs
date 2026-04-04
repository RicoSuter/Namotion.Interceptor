using System.IO.Compression;
using Xunit;
using Namotion.NuGet.Plugins.Loading;

namespace Namotion.NuGet.Plugins.Tests.Loading;

public class PackageExtractorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PackageExtractor _extractor;

    public PackageExtractorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuGetPluginsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _extractor = new PackageExtractor(_tempDir);
    }

    [Fact]
    public void WhenExtractingPackageWithNet9Dll_ThenReturnsDllPath()
    {
        // Arrange
        var stream = CreateTestNupkg("TestPkg", "1.0.0", "net9.0");

        // Act
        var paths = _extractor.ExtractAndGetAssemblyPaths("TestPkg", "1.0.0", stream);

        // Assert
        Assert.Single(paths);
        Assert.EndsWith(".dll", paths[0]);
    }

    [Fact]
    public void WhenPackageAlreadyCached_ThenDoesNotReExtract()
    {
        // Arrange
        var stream1 = CreateTestNupkg("TestPkg", "1.0.0", "net9.0");
        _extractor.ExtractAndGetAssemblyPaths("TestPkg", "1.0.0", stream1);

        var stream2 = CreateTestNupkg("TestPkg", "1.0.0", "net9.0");

        // Act
        var paths = _extractor.ExtractAndGetAssemblyPaths("TestPkg", "1.0.0", stream2);

        // Assert
        Assert.Single(paths);
    }

    [Fact]
    public void WhenPackageHasMultipleFrameworks_ThenPrefersHighestCompatible()
    {
        // Arrange
        var stream = CreateTestNupkgMultiTfm("TestPkg", "1.0.0", ["net8.0", "net9.0"]);

        // Act
        var paths = _extractor.ExtractAndGetAssemblyPaths("TestPkg", "1.0.0", stream);

        // Assert
        Assert.Single(paths);
        Assert.Contains("net9.0", paths[0]);
    }

    [Fact]
    public void WhenPackageHasNoLibFolder_ThenReturnsEmpty()
    {
        // Arrange
        var stream = CreateEmptyNupkg("TestPkg", "1.0.0");

        // Act
        var paths = _extractor.ExtractAndGetAssemblyPaths("TestPkg", "1.0.0", stream);

        // Assert
        Assert.Empty(paths);
    }

    [Fact]
    public void WhenCheckingCachedPackage_ThenReturnsPathIfExists()
    {
        // Arrange
        var stream = CreateTestNupkg("TestPkg", "1.0.0", "net9.0");
        _extractor.ExtractAndGetAssemblyPaths("TestPkg", "1.0.0", stream);

        // Act
        var cached = _extractor.GetCachedPackagePath("TestPkg", "1.0.0");
        var notCached = _extractor.GetCachedPackagePath("Other", "1.0.0");

        // Assert
        Assert.NotNull(cached);
        Assert.Null(notCached);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static MemoryStream CreateTestNupkg(string name, string version, string tfm)
    {
        var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Add a nuspec
            var nuspecEntry = archive.CreateEntry($"{name}.nuspec");
            using (var writer = new StreamWriter(nuspecEntry.Open()))
            {
                writer.Write($"""
                    <?xml version="1.0"?>
                    <package><metadata><id>{name}</id><version>{version}</version></metadata></package>
                    """);
            }

            // Add a fake DLL in the right TFM folder
            var dllEntry = archive.CreateEntry($"lib/{tfm}/{name}.dll");
            using (var writer = new StreamWriter(dllEntry.Open()))
            {
                writer.Write("fake dll content");
            }
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    private static MemoryStream CreateTestNupkgMultiTfm(string name, string version, string[] tfms)
    {
        var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nuspecEntry = archive.CreateEntry($"{name}.nuspec");
            using (var writer = new StreamWriter(nuspecEntry.Open()))
            {
                writer.Write($"""
                    <?xml version="1.0"?>
                    <package><metadata><id>{name}</id><version>{version}</version></metadata></package>
                    """);
            }

            foreach (var tfm in tfms)
            {
                var dllEntry = archive.CreateEntry($"lib/{tfm}/{name}.dll");
                using var writer = new StreamWriter(dllEntry.Open());
                writer.Write("fake dll content");
            }
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    private static MemoryStream CreateEmptyNupkg(string name, string version)
    {
        var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nuspecEntry = archive.CreateEntry($"{name}.nuspec");
            using var writer = new StreamWriter(nuspecEntry.Open());
            writer.Write($"""
                <?xml version="1.0"?>
                <package><metadata><id>{name}</id><version>{version}</version></metadata></package>
                """);
        }

        memoryStream.Position = 0;
        return memoryStream;
    }
}
