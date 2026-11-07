using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Microsoft.AspNetCore.Http;

namespace HomeBlaze.Storage.Blazor.Tests;

public class StorageFileRequestHandlerTests
{
    // A 4x2 red PNG.
    private static readonly byte[] PngContent = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAACCAIAAADwyuo0AAAAEElEQVR4nGP4z8AARwzIHABvqgf5gNwAKAAAAABJRU5ErkJggg==");

    private static readonly DateTime Modified = new(2026, 11, 7, 8, 30, 0, DateTimeKind.Utc);

    private static GenericFile CreateFile(FakeStorageContainer storage, string path, long size) =>
        new(storage, path) { FileSize = size, LastModified = Modified };

    private static DefaultHttpContext CreateContext(string? ifNoneMatch = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (ifNoneMatch is not null)
        {
            context.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        return context;
    }

    private static byte[] ReadBody(HttpContext context) => ((MemoryStream)context.Response.Body).ToArray();

    [Fact]
    public async Task WhenSubjectIsABinaryFile_ThenItsContentIsServedWithItsTypeAndHeaders()
    {
        // Arrange
        var storage = new FakeStorageContainer(PngContent);
        var file = CreateFile(storage, "images/plan.png", PngContent.Length);
        var context = CreateContext();

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        var response = context.Response;
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("image/png", response.ContentType);
        Assert.Equal(PngContent.Length, response.ContentLength);
        Assert.Equal(PngContent, ReadBody(context));
        Assert.StartsWith("W/\"", response.Headers.ETag.ToString());
        Assert.Equal("Sat, 07 Nov 2026 08:30:00 GMT", response.Headers.LastModified.ToString());
        Assert.Equal("no-cache", response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", response.Headers.XContentTypeOptions.ToString());
        Assert.Equal("sandbox", response.Headers.ContentSecurityPolicy.ToString());
    }

    [Fact]
    public async Task WhenSubjectIsAPdfFile_ThenItIsServedWithoutASandbox()
    {
        // Arrange
        var storage = new FakeStorageContainer(PngContent);
        var file = CreateFile(storage, "Docs/manual.pdf", PngContent.Length);
        var context = CreateContext();

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        var response = context.Response;
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("application/pdf", response.ContentType);
        Assert.Equal("nosniff", response.Headers.XContentTypeOptions.ToString());
        Assert.False(response.Headers.ContainsKey("Content-Security-Policy"));
    }

    [Theory]
    [InlineData("notes.txt", "text/plain; charset=utf-8")]
    [InlineData("data.csv", "text/csv; charset=utf-8")]
    [InlineData("plan.png", "image/png")]
    public async Task WhenFileIsServed_ThenATextTypeHasTheUtf8CharsetAndAnyOtherTypeIsUnchanged(string name, string expected)
    {
        // Arrange
        var storage = new FakeStorageContainer(PngContent);
        var file = CreateFile(storage, name, PngContent.Length);
        var context = CreateContext();

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        Assert.Equal(expected, context.Response.ContentType);
    }

    [Theory]
    [InlineData("text/x-vcard", "text/x-vcard; charset=utf-8")]
    [InlineData("text/plain; charset=iso-8859-1", "text/plain; charset=iso-8859-1")]
    [InlineData("application/json", "application/json")]
    public void WhenContentTypeIsGiven_ThenOnlyATextTypeWithoutCharsetGetsUtf8(string contentType, string expected)
    {
        // Act
        var result = StorageFileRequestHandler.WithCharset(contentType);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/Demo/x.png", "Demo/x.png")]
    [InlineData("//Demo/x.png", "Demo/x.png")]
    [InlineData("Demo/x.png", "Demo/x.png")]
    [InlineData(null, "")]
    public void WhenRouteValueIsGiven_ThenSubjectPathHasNoLeadingSlash(string? routeValue, string expected)
    {
        // Act
        var path = StorageFileRequestHandler.GetSubjectPath(routeValue);

        // Assert
        Assert.Equal(expected, path);
    }

    [Fact]
    public async Task WhenStorageReportsNoModificationTime_ThenTheFileIsSentInFullWithoutValidators()
    {
        // Arrange
        var storage = new FakeStorageContainer(PngContent);
        var file = new GenericFile(storage, "plan.png") { FileSize = PngContent.Length };
        var context = CreateContext(ifNoneMatch: "*");

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        var response = context.Response;
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(PngContent, ReadBody(context));
        Assert.False(response.Headers.ContainsKey("ETag"));
        Assert.False(response.Headers.ContainsKey("Last-Modified"));
        Assert.Equal("no-cache", response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", response.Headers.XContentTypeOptions.ToString());
        Assert.Equal(1, storage.ReadCount);
    }

    [Fact]
    public async Task WhenRequestHasTheCurrentETag_ThenItIsNotModifiedAndTheStorageIsNotRead()
    {
        // Arrange
        var storage = new FakeStorageContainer(PngContent);
        var file = CreateFile(storage, "plan.png", PngContent.Length);
        var first = CreateContext();
        await StorageFileRequestHandler.HandleAsync(first, file);
        var context = CreateContext(ifNoneMatch: first.Response.Headers.ETag.ToString());

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
        Assert.Empty(ReadBody(context));
        Assert.Equal(first.Response.Headers.ETag.ToString(), context.Response.Headers.ETag.ToString());
        Assert.Equal(1, storage.ReadCount);
    }

    [Fact]
    public async Task WhenRequestHasADifferentETag_ThenTheContentIsServed()
    {
        // Arrange
        var storage = new FakeStorageContainer(PngContent);
        var file = CreateFile(storage, "plan.png", PngContent.Length);
        var context = CreateContext(ifNoneMatch: "W/\"other\"");

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(PngContent, ReadBody(context));
    }

    [Fact]
    public async Task WhenFileIsReplacedBySomethingOfAnotherSize_ThenTheETagChanges()
    {
        // Arrange
        var storage = new FakeStorageContainer(PngContent);
        var file = CreateFile(storage, "plan.png", PngContent.Length);
        var before = CreateContext();
        await StorageFileRequestHandler.HandleAsync(before, file);
        file.FileSize = PngContent.Length + 1;
        var after = CreateContext();

        // Act
        await StorageFileRequestHandler.HandleAsync(after, file);

        // Assert
        Assert.NotEqual(before.Response.Headers.ETag.ToString(), after.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task WhenExtensionIsUnknown_ThenItIsServedAsBinaryData()
    {
        // Arrange
        var storage = new FakeStorageContainer([1, 2, 3]);
        var file = CreateFile(storage, "data.unknownextension", 3);
        var context = CreateContext();

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        Assert.Equal("application/octet-stream", context.Response.ContentType);
    }

    [Fact]
    public async Task WhenNoSubjectIsFound_ThenItIsNotFound()
    {
        // Arrange
        var context = CreateContext();

        // Act
        await StorageFileRequestHandler.HandleAsync(context, null);

        // Assert
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Empty(ReadBody(context));
    }

    [Fact]
    public async Task WhenSubjectIsAJsonFile_ThenItIsNotFoundAndTheStorageIsNotRead()
    {
        // Arrange
        var storage = new FakeStorageContainer("{\"apiKey\":\"secret\"}"u8.ToArray());
        var file = new JsonFile(storage, "secrets.json");
        var context = CreateContext();

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Empty(ReadBody(context));
        Assert.Equal(0, storage.ReadCount);
    }

    [Fact]
    public async Task WhenStorageIsNotConnected_ThenItIsUnavailableAndTheStorageIsNotRead()
    {
        // Arrange
        var storage = new FakeStorageContainer(PngContent) { Status = StorageStatus.Disconnected };
        var file = CreateFile(storage, "plan.png", PngContent.Length);
        var context = CreateContext();

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal(0, storage.ReadCount);
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    public async Task WhenFileOrFolderIsGoneAtRead_ThenItIsNotFoundWithoutCacheHeaders(Type exceptionType)
    {
        // Arrange
        var storage = new FakeStorageContainer(PngContent) { ReadException = (Exception)Activator.CreateInstance(exceptionType)! };
        var file = CreateFile(storage, "plan.png", PngContent.Length);
        var context = CreateContext();

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task WhenFileIsGoneAtReadWithNoStream_ThenItIsNotFound()
    {
        // Arrange
        var storage = new FakeStorageContainer(content: null);
        var file = CreateFile(storage, "plan.png", PngContent.Length);
        var context = CreateContext();

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task WhenReadFailsInTheStorage_ThenItIsUnavailableWithoutCacheHeaders(Type exceptionType)
    {
        // Arrange
        var storage = new FakeStorageContainer(PngContent) { ReadException = (Exception)Activator.CreateInstance(exceptionType)! };
        var file = CreateFile(storage, "plan.png", PngContent.Length);
        var context = CreateContext();

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task WhenReadThrowsInvalidOperationWhileStorageIsConnected_ThenItPropagates()
    {
        // Arrange
        var storage = new FakeStorageContainer(PngContent) { ReadException = new InvalidOperationException() };
        var file = CreateFile(storage, "plan.png", PngContent.Length);
        var context = CreateContext();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => StorageFileRequestHandler.HandleAsync(context, file));
    }

    [Fact]
    public async Task WhenStorageDisconnectsDuringRead_ThenItIsUnavailableWithoutCacheHeaders()
    {
        // Arrange
        var storage = new FakeStorageContainer(PngContent)
        {
            ReadException = new InvalidOperationException(),
            StatusWhenReadStarts = StorageStatus.Disconnected
        };
        var file = CreateFile(storage, "plan.png", PngContent.Length);
        var context = CreateContext();

        // Act
        await StorageFileRequestHandler.HandleAsync(context, file);

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
    }
}
