using HomeBlaze.AI.Mcp;
using HomeBlaze.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Namotion.Interceptor.Mcp.Extensions;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class HomeBlazeMcpToolAnnotationTests
{
    [Fact]
    public async Task WhenAClientListsTheToolsOverHttp_ThenReadToolsAreReadOnlyAndWriteToolsDestructive()
    {
        // Arrange
        var (room, _, _) = HomeBlazeMcpToolProviderTests.CreateTestSetup(isReadOnly: false);
        var services = new ServiceCollection()
            .AddSingleton(new SubjectTypeRegistry(new TypeProvider()))
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();
        var configuration = HomeBlazeMcpConfigurationFactory.Create(services, () => room, isReadOnly: false);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithSubjectRegistryTools(() => room, configuration);
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(address + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);

        // Act
        var tools = (await client.ListToolsAsync()).ToDictionary(tool => tool.Name, tool => tool.ProtocolTool.Annotations);

        // Assert
        foreach (var name in new[] { "browse", "search", "get_property", "list_types", "list_methods", "invoke_query" })
        {
            Assert.True(tools[name]!.ReadOnlyHint, name);
            Assert.Null(tools[name]!.DestructiveHint);
            Assert.False(tools[name]!.OpenWorldHint, name);
        }

        foreach (var name in new[] { "set_property", "invoke_operation", "edit_markdown", "write_markdown", "create_markdown" })
        {
            Assert.False(tools[name]!.ReadOnlyHint, name);
            Assert.True(tools[name]!.DestructiveHint, name);
            Assert.False(tools[name]!.OpenWorldHint, name);
        }

        Assert.All(tools.Values, annotations => Assert.Null(annotations!.IdempotentHint));
    }
}
