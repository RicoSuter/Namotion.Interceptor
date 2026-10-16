using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.AI.Mcp;
using HomeBlaze.AI.Tests.Agents;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public sealed class HomeBlazeContentSearchToolTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-content-search-");
    private readonly List<FluentStorageContainer> _storages = [];

    [Fact]
    public async Task WhenSearchingAPhrase_ThenEachMatchingPageIsReturnedWithItsLinesAndSnippets()
    {
        // Arrange
        var tool = await CreateToolAsync();

        // Act
        var result = await CallAsync(tool, new { query = "freezer" });

        // Assert
        var page = Assert.Single(result.GetProperty("pages").EnumerateArray());
        Assert.Equal("/Storage/Notes.md", page.GetProperty("path").GetString());
        Assert.Equal("Notes", page.GetProperty("title").GetString());
        Assert.Equal(2, page.GetProperty("match_count").GetInt32());
        Assert.Equal([3, 4], page.GetProperty("matches").EnumerateArray().Select(match => match.GetProperty("line").GetInt32()));
        Assert.Equal(
            ["The freezer is cold.", "The FREEZER door sticks."],
            page.GetProperty("matches").EnumerateArray().Select(match => match.GetProperty("snippet").GetString()));
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(3, result.GetProperty("pages_searched").GetInt32());
    }

    [Fact]
    public async Task WhenIgnoreCaseIsFalse_ThenOnlyLinesWithTheExactCaseMatch()
    {
        // Arrange
        var tool = await CreateToolAsync();

        // Act
        var result = await CallAsync(tool, new { query = "FREEZER", ignore_case = false });

        // Assert
        var page = Assert.Single(result.GetProperty("pages").EnumerateArray());
        Assert.Equal(1, page.GetProperty("match_count").GetInt32());
        Assert.Equal(4, Assert.Single(page.GetProperty("matches").EnumerateArray()).GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task WhenSearchingARegularExpression_ThenThePagesWithMatchingLinesAreReturnedInPathOrder()
    {
        // Arrange
        var tool = await CreateToolAsync();

        // Act
        var result = await CallAsync(tool, new { query = @"heat\s+pump (schedule|was)", regex = true });

        // Assert
        Assert.Equal(
            ["/Storage/Docs/HeatPump.md", "/Storage/DocsArchive/Old.md"],
            result.GetProperty("pages").EnumerateArray().Select(page => page.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task WhenTheRegularExpressionIsInvalid_ThenAnErrorIsReturned()
    {
        // Arrange
        var tool = await CreateToolAsync();

        // Act
        var result = await CallAsync(tool, new { query = "(", regex = true });

        // Assert
        Assert.StartsWith("Invalid regular expression:", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenTheRegularExpressionBacktracksCatastrophically_ThenATimeoutErrorIsReturned()
    {
        // Arrange
        var tool = await CreateToolAsync(("Slow.md", new string('a', 40) + "!"));

        // Act
        var result = await CallAsync(tool, new { query = "^(a|aa)+$", regex = true });

        // Assert
        Assert.Equal(
            "The regular expression took longer than 100 ms on a line of '/Storage/Slow.md'; simplify it.",
            result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenTheQueryIsMissing_ThenAnErrorIsReturned()
    {
        // Arrange
        var tool = await CreateToolAsync();

        // Act
        var result = await CallAsync(tool, new { query = "" });

        // Assert
        Assert.Equal("Missing parameter 'query'.", result.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("/Storage/Docs")]
    [InlineData("Storage/Docs/")]
    [InlineData("/storage/docs")]
    public async Task WhenAPathIsGiven_ThenOnlyPagesInOrUnderItAreSearched(string path)
    {
        // Arrange
        var tool = await CreateToolAsync();

        // Act
        var result = await CallAsync(tool, new { query = "heat pump", path });

        // Assert
        var page = Assert.Single(result.GetProperty("pages").EnumerateArray());
        Assert.Equal("/Storage/Docs/HeatPump.md", page.GetProperty("path").GetString());
        Assert.Equal(1, result.GetProperty("pages_searched").GetInt32());
    }

    [Fact]
    public async Task WhenMorePagesMatchThanMaxPages_ThenTheResultIsTruncated()
    {
        // Arrange
        var tool = await CreateToolAsync();

        // Act
        var result = await CallAsync(tool, new { query = "heat pump", max_pages = 1 });

        // Assert
        var page = Assert.Single(result.GetProperty("pages").EnumerateArray());
        Assert.Equal("/Storage/Docs/HeatPump.md", page.GetProperty("path").GetString());
        Assert.True(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task WhenAPageHasMoreMatchingLinesThanTheLimit_ThenTheMatchCountIncludesAllOfThem()
    {
        // Arrange
        var tool = await CreateToolAsync(("Many.md", string.Join("\n", Enumerable.Repeat("a line with a needle", 8))));

        // Act
        var result = await CallAsync(tool, new { query = "needle" });

        // Assert
        var page = Assert.Single(result.GetProperty("pages").EnumerateArray());
        Assert.Equal(8, page.GetProperty("match_count").GetInt32());
        Assert.Equal([1, 2, 3, 4, 5], page.GetProperty("matches").EnumerateArray().Select(match => match.GetProperty("line").GetInt32()));
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task WhenAMatchIsInALongLine_ThenTheSnippetIsCutAroundIt()
    {
        // Arrange
        var tool = await CreateToolAsync(("Long.md", new string('x', 300) + " needle " + new string('y', 300)));

        // Act
        var result = await CallAsync(tool, new { query = "needle" });

        // Assert
        var snippet = Assert.Single(Assert.Single(result.GetProperty("pages").EnumerateArray()).GetProperty("matches").EnumerateArray())
            .GetProperty("snippet").GetString()!;
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.Contains(" needle ", snippet);
        Assert.Equal(162, snippet.Length);
    }

    [Fact]
    public async Task WhenAPageHasASecretConfigurationValue_ThenTheValueIsNeitherMatchedNorShown()
    {
        // Arrange
        var device = typeof(ContentSearchSecretDevice).FullName;
        var tool = await CreateToolAsync(("Gateway.md", $":: {device}#gateway Host=gateway-host Password=s3cr3t\n::\n"));

        // Act
        var secretSearch = await CallAsync(tool, new { query = "s3cr3t" });
        var hostSearch = await CallAsync(tool, new { query = "gateway-host" });

        // Assert
        Assert.Empty(secretSearch.GetProperty("pages").EnumerateArray());
        var snippet = Assert.Single(Assert.Single(hostSearch.GetProperty("pages").EnumerateArray()).GetProperty("matches").EnumerateArray())
            .GetProperty("snippet").GetString();
        Assert.Contains("***", snippet);
        Assert.DoesNotContain("s3cr3t", snippet);
    }

    [Fact]
    public async Task WhenTheConfigurationIsReadOnly_ThenSearchContentIsOfferedAsAReadOnlyTool()
    {
        // Arrange
        var (root, services) = await CreateGraphAsync([]);

        // Act
        var tools = CreateTools(services, root, isReadOnly: true);

        // Assert
        Assert.Same(McpToolAnnotations.ReadOnly, tools[HomeBlazeContentSearchToolProvider.SearchContentToolName].Annotations);
    }

    private async Task<McpToolInfo> CreateToolAsync(params (string Path, string Content)[] additionalFiles)
    {
        var (root, services) = await CreateGraphAsync(additionalFiles);
        return CreateTools(services, root, isReadOnly: true)[HomeBlazeContentSearchToolProvider.SearchContentToolName];
    }

    private async Task<(AgentTestRoot Root, IServiceProvider Services)> CreateGraphAsync((string Path, string Content)[] additionalFiles)
    {
        WriteFile("Notes.md", "# Notes\n\nThe freezer is cold.\nThe FREEZER door sticks.");
        WriteFile("Docs/HeatPump.md", "# Heat pump\n\nThe heat pump schedule starts at 06:00.");
        WriteFile("DocsArchive/Old.md", "# Old\n\nThe heat pump was replaced.");
        foreach (var (path, content) in additionalFiles)
        {
            WriteFile(path, content);
        }

        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        typeProvider.AddAssembly(typeof(ContentSearchSecretDevice).Assembly);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        AgentTestRoot? root = null;
        services.AddSingleton(new SubjectPathResolver(() => root));
        services.AddSingleton<MarkdownContentParser>();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var serviceProvider = services.BuildServiceProvider();

        var storage = new FluentStorageContainer(typeRegistry, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider)
        {
            ConnectionString = _directory.FullName,
            EnableFileWatching = false
        };
        _storages.Add(storage);

        root = new AgentTestRoot(context) { Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage } };
        await storage.ConnectAsync(CancellationToken.None);
        return (root, serviceProvider);
    }

    private static Dictionary<string, McpToolInfo> CreateTools(IServiceProvider services, AgentTestRoot root, bool isReadOnly)
    {
        var configuration = HomeBlazeMcpConfigurationFactory.Create(services, () => root, isReadOnly, forAgents: true);
        return new McpToolFactory(root, configuration).CreateTools().ToDictionary(tool => tool.Name);
    }

    private static async Task<JsonElement> CallAsync(McpToolInfo tool, object input) =>
        JsonSerializer.SerializeToElement(await tool.Handler(JsonSerializer.SerializeToElement(input), CancellationToken.None));

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public void Dispose()
    {
        foreach (var storage in _storages)
        {
            storage.Dispose();
        }

        _directory.Delete(recursive: true);
    }
}

[InterceptorSubject]
public partial class ContentSearchSecretDevice : IConfigurable
{
    [Configuration]
    public partial string? Host { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
