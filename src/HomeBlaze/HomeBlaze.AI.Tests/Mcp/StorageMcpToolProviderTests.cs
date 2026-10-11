using System.Text;
using System.Text.Json;
using HomeBlaze.AI.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class StorageMcpToolProviderTests
{
    private const int MaxBytes = 64 * 1024;

    [Fact]
    public async Task WhenReadingAMarkdownFile_ThenReturnsItsText()
    {
        // Arrange
        var factory = CreateFactory(isReadOnly: false, new TestStorageFile { Name = "home.md", Content = Encoding.UTF8.GetBytes("# Home\n\nWelcome. Grüezi!") });

        // Act
        var result = await ReadFileAsync(factory, "/Docs/home.md");

        // Assert
        Assert.Equal("# Home\n\nWelcome. Grüezi!", Assert.IsType<string>(result));
    }

    [Fact]
    public async Task WhenAFileWasRead_ThenItsStreamIsDisposed()
    {
        // Arrange
        var file = new TestStorageFile { Name = "home.md", Content = Encoding.UTF8.GetBytes("Home") };
        var factory = CreateFactory(isReadOnly: false, file);

        // Act
        await ReadFileAsync(factory, "/Docs/home.md");

        // Assert
        Assert.True(file.IsStreamDisposed);
    }

    [Fact]
    public async Task WhenTheTokenIsCancelled_ThenCancellationPropagates()
    {
        // Arrange
        var factory = CreateFactory(isReadOnly: false, new TestStorageFile { Name = "home.md", Content = Encoding.UTF8.GetBytes("Home") });
        var tool = factory.CreateTools().First(candidate => candidate.Name == "read_file");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tool.Handler(JsonSerializer.SerializeToElement(new { path = "/Docs/home.md" }), cancellation.Token));
    }

    [Fact]
    public async Task WhenTheStorageReturnsNoStream_ThenReturnsFileNotFound()
    {
        // Arrange
        var logger = new RecordingLogger<StorageMcpToolProvider>();
        var factory = CreateFactory(isReadOnly: false, new TestStorageFile { Name = "gone.md", ReturnsNullStream = true }, logger);

        // Act
        var result = await ReadFileAsync(factory, "/Docs/gone.md");

        // Assert
        Assert.Equal("File not found.", GetError(result));
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public async Task WhenTheFullPathIsNotMarkdown_ThenReturnsAnErrorWithoutReading()
    {
        // Arrange
        var file = new TestStorageFile { Name = "notes.md", FullPath = "secrets.json", Content = Encoding.UTF8.GetBytes("SECRET-VALUE") };
        var factory = CreateFactory(isReadOnly: false, file);

        // Act
        var result = await ReadFileAsync(factory, "/Docs/notes.md");

        // Assert
        Assert.Contains("Markdown", GetError(result));
        Assert.Equal(0, file.BytesRead);
    }

    [Fact]
    public async Task WhenTheExtensionIsUpperCase_ThenReturnsItsText()
    {
        // Arrange
        var factory = CreateFactory(isReadOnly: false, new TestStorageFile { Name = "README.MD", Content = Encoding.UTF8.GetBytes("Read me") });

        // Act
        var result = await ReadFileAsync(factory, "/Docs/README.MD");

        // Assert
        Assert.Equal("Read me", Assert.IsType<string>(result));
    }

    [Fact]
    public async Task WhenTheFileStartsWithAByteOrderMark_ThenTheMarkIsNotReturned()
    {
        // Arrange
        var content = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("Text")).ToArray();
        var factory = CreateFactory(isReadOnly: false, new TestStorageFile { Name = "bom.md", Content = content });

        // Act
        var result = await ReadFileAsync(factory, "/Docs/bom.md");

        // Assert
        Assert.Equal("Text", Assert.IsType<string>(result));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("config.json")]
    [InlineData("secrets.md.json")]
    [InlineData("notes.md.")]
    [InlineData("notes.md ")]
    [InlineData("md")]
    public async Task WhenTheFileIsNotMarkdown_ThenReturnsAnErrorWithoutContent(string fileName)
    {
        // Arrange
        var file = new TestStorageFile { Name = fileName, Content = Encoding.UTF8.GetBytes("SECRET-VALUE") };
        var factory = CreateFactory(isReadOnly: false, file);

        // Act
        var result = await ReadFileAsync(factory, "/Docs/" + fileName);

        // Assert
        var error = GetError(result);
        Assert.Contains("Markdown", error);
        Assert.DoesNotContain("SECRET-VALUE", JsonSerializer.Serialize(result));
        Assert.Equal(0, file.BytesRead);
    }

    [Fact]
    public async Task WhenThePathIsNotAFile_ThenReturnsAnError()
    {
        // Arrange
        var factory = CreateFactory(isReadOnly: false, new TestStorageFile { Name = "home.md" });

        // Act
        var result = await ReadFileAsync(factory, "/Docs");

        // Assert
        Assert.Contains("Markdown", GetError(result));
    }

    [Fact]
    public async Task WhenThePathIsUnknown_ThenReturnsNotFound()
    {
        // Arrange
        var factory = CreateFactory(isReadOnly: false, new TestStorageFile { Name = "home.md" });

        // Act
        var result = await ReadFileAsync(factory, "/Docs/missing.md");

        // Assert
        Assert.Contains("not found", GetError(result));
    }

    [Fact]
    public async Task WhenThePathIsMissing_ThenReturnsAnError()
    {
        // Arrange
        var factory = CreateFactory(isReadOnly: false, new TestStorageFile { Name = "home.md" });
        var tool = factory.CreateTools().First(candidate => candidate.Name == "read_file");

        // Act
        var result = await tool.Handler(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        // Assert
        Assert.Contains("path", GetError(result));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenTheFileIsLargerThanTheCap_ThenReturnsTheFirstPartWithATruncationMarker(bool isSeekable)
    {
        // Arrange
        var content = Encoding.UTF8.GetBytes(new string('a', MaxBytes * 4));
        var file = new TestStorageFile { Name = "large.md", Content = content, FileSize = content.Length, IsSeekable = isSeekable };
        var factory = CreateFactory(isReadOnly: false, file);

        // Act
        var result = await ReadFileAsync(factory, "/Docs/large.md");

        // Assert
        var text = Assert.IsType<string>(result);
        Assert.Equal(new string('a', MaxBytes) + $"\n\n[truncated: file has {content.Length} bytes]", text);
        Assert.True(file.BytesRead <= MaxBytes + 16, $"Read {file.BytesRead} bytes.");
    }

    [Fact]
    public async Task WhenTheFileIsExactlyTheCap_ThenItIsNotTruncated()
    {
        // Arrange
        var content = Encoding.UTF8.GetBytes(new string('a', MaxBytes));
        var factory = CreateFactory(isReadOnly: false, new TestStorageFile { Name = "exact.md", Content = content });

        // Act
        var result = await ReadFileAsync(factory, "/Docs/exact.md");

        // Assert
        Assert.Equal(new string('a', MaxBytes), Assert.IsType<string>(result));
    }

    [Fact]
    public async Task WhenTheCapSplitsAMultiByteCharacter_ThenTheCharacterIsDropped()
    {
        // Arrange
        var content = Encoding.UTF8.GetBytes(new string('a', MaxBytes - 1) + "€€");
        var factory = CreateFactory(isReadOnly: false, new TestStorageFile { Name = "split.md", Content = content });

        // Act
        var result = await ReadFileAsync(factory, "/Docs/split.md");

        // Assert
        var text = Assert.IsType<string>(result);
        Assert.DoesNotContain('\uFFFD', text);
        Assert.DoesNotContain('€', text);
        Assert.StartsWith(new string('a', MaxBytes - 1) + "\n", text);
    }

    [Fact]
    public async Task WhenReadingTheFileFails_ThenTheErrorDoesNotContainTheExceptionMessage()
    {
        // Arrange
        var file = new TestStorageFile { Name = "broken.md", ReadException = new InvalidOperationException("SECRET-DETAIL") };
        var logger = new RecordingLogger<StorageMcpToolProvider>();
        var factory = CreateFactory(isReadOnly: false, file, logger);

        // Act
        var result = await ReadFileAsync(factory, "/Docs/broken.md");

        // Assert
        var error = GetError(result);
        Assert.DoesNotContain("SECRET-DETAIL", error);
        Assert.Single(logger.Errors);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task WhenAnInputOutputFailureOccurs_ThenItIsLoggedAsAWarning(Type exceptionType)
    {
        // Arrange
        var exception = (Exception)Activator.CreateInstance(exceptionType, "SECRET-DETAIL")!;
        var logger = new RecordingLogger<StorageMcpToolProvider>();
        var factory = CreateFactory(isReadOnly: false, new TestStorageFile { Name = "broken.md", ReadException = exception }, logger);

        // Act
        var result = await ReadFileAsync(factory, "/Docs/broken.md");

        // Assert
        Assert.DoesNotContain("SECRET-DETAIL", GetError(result));
        Assert.Single(logger.Warnings);
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public async Task WhenTheConfigurationIsReadOnly_ThenTheToolIsOfferedAndReads()
    {
        // Arrange
        var factory = CreateFactory(isReadOnly: true, new TestStorageFile { Name = "home.md", Content = Encoding.UTF8.GetBytes("Home") });

        // Act
        var result = await ReadFileAsync(factory, "/Docs/home.md");

        // Assert
        Assert.Equal("Home", Assert.IsType<string>(result));
    }

    private static McpToolFactory CreateFactory(bool isReadOnly, TestStorageFile file, ILogger<StorageMcpToolProvider>? logger = null)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new TestStorageFolder(context);
        var docs = new TestStorageFolder();
        docs.Children = new Dictionary<string, IInterceptorSubject> { [file.Name] = file };
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Docs"] = docs };

        var pathProvider = new StateAttributePathProvider();
        var configuration = new McpServerConfiguration
        {
            PathProvider = pathProvider,
            PathPrefix = "/",
            IsReadOnly = isReadOnly,
            ToolProviders = { new StorageMcpToolProvider(() => root, pathProvider, logger ?? NullLogger<StorageMcpToolProvider>.Instance) }
        };

        return new McpToolFactory(root, configuration);
    }

    private static Task<object?> ReadFileAsync(McpToolFactory factory, string path)
    {
        var tool = factory.CreateTools().First(candidate => candidate.Name == "read_file");
        return tool.Handler(JsonSerializer.SerializeToElement(new { path }), CancellationToken.None);
    }

    private static string GetError(object? result) =>
        JsonSerializer.SerializeToElement(result).GetProperty("error").GetString()!;
}
