using System.Text.Json;
using HomeBlaze.AI.Mcp;
using HomeBlaze.AI.Tests.Agents;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public sealed class HomeBlazeMetadataSubjectToolTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-metadata-tools-");
    private readonly List<FluentStorageContainer> _storages = [];

    [Fact]
    public async Task WhenAStoredSubjectIsBrowsedAtDepthZero_ThenItsMetadataSubjectIsNotListed()
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        var tools = await CreateToolsAsync();

        // Act
        var result = await CallAsync(tools["browse"], new { path = "/Storage/Docs/Pump.json", depth = 0, format = "json" });

        // Assert
        var node = result.GetProperty("result");
        Assert.False(node.TryGetProperty("properties", out var properties) && properties.TryGetProperty(IMetadataSubject.PropertyName, out _));
    }

    [Fact]
    public async Task WhenSetMetadataMeetsAFileThatCannotBeRead_ThenTheAgentIsToldWhy()
    {
        // Arrange
        WriteFile("Docs/Readme.md", "# Readme");
        WriteFile("Docs/.metadata.json", "not json");
        var tools = await CreateToolsAsync();

        // Act
        var result = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage/Docs/HomeBlaze:Metadata",
            method = "SetMetadata",
            parameters = new { values = new { title = "Docs" } }
        });

        // Assert
        Assert.Contains("could not be read", result.GetProperty("error").GetString());
        Assert.Equal("not json", await File.ReadAllTextAsync(Path.Combine(_directory.FullName, "Docs", ".metadata.json")));
    }

    private async Task<Dictionary<string, McpToolInfo>> CreateToolsAsync()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
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
        var configuration = HomeBlazeMcpConfigurationFactory.Create(serviceProvider, () => root, isReadOnly: false, forAgents: false);
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
