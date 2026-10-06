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
        var stream = CreateTestNupkg("TestPkg", "1.0.0", "net8.0", "net9.0");

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
        var stream = CreateTestNupkg("TestPkg", "1.0.0");

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

    [Fact]
    public async Task WhenSamePackageIsExtractedConcurrently_ThenEveryCallSeesAllFiles()
    {
        // Arrange
        var package = CreatePackageWithManyAssemblies(fileCount: 200);

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            _extractor.ExtractAndGetAssemblyPaths("Test.Package", "1.0.0", new MemoryStream(package)))));

        // Assert
        Assert.All(results, paths => Assert.Equal(200, paths.Count));
    }

    [Fact]
    public void WhenExtractionFails_ThenNoPackageDirectoryIsLeftBehind()
    {
        // Arrange
        var corruptPackage = new MemoryStream([1, 2, 3, 4]);

        // Act & Assert
        Assert.ThrowsAny<InvalidDataException>(() =>
            _extractor.ExtractAndGetAssemblyPaths("Test.Package", "1.0.0", corruptPackage));
        Assert.Null(_extractor.GetCachedPackagePath("Test.Package", "1.0.0"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_tempDir, "Test.Package")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static MemoryStream CreateTestNupkg(string name, string version, params string[] tfms)
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

    private static byte[] CreatePackageWithManyAssemblies(int fileCount)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < fileCount; index++)
            {
                var entry = archive.CreateEntry($"lib/net10.0/Assembly{index}.dll");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(new string('x', 4096));
            }
        }

        return stream.ToArray();
    }
}
