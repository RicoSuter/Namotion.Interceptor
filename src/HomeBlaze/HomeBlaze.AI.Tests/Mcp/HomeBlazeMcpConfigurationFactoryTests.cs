using HomeBlaze.AI.Mcp;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class HomeBlazeMcpConfigurationFactoryTests
{
    [Fact]
    public void WhenCreatingAConfiguration_ThenRegisteredProvidersAndTheReadOnlyFlagApply()
    {
        // Arrange
        var (room, _, _) = HomeBlazeMcpToolProviderTests.CreateTestSetup(isReadOnly: true);
        var services = new ServiceCollection()
            .AddSingleton(new SubjectTypeRegistry(new TypeProvider()))
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddHomeBlazeMcpToolProvider(_ => new StubToolProvider())
            .BuildServiceProvider();

        // Act
        var configuration = HomeBlazeMcpConfigurationFactory.Create(services, () => room, isReadOnly: false);
        var toolNames = new McpToolFactory(room, configuration).CreateTools().Select(tool => tool.Name).ToArray();

        // Assert
        Assert.False(configuration.IsReadOnly);
        Assert.Contains("invoke_method", toolNames);
        Assert.Contains("stub_tool", toolNames);
    }

    private sealed class StubToolProvider : IMcpToolProvider
    {
        public IEnumerable<Namotion.Interceptor.Mcp.McpToolInfo> GetTools()
        {
            yield return new Namotion.Interceptor.Mcp.McpToolInfo
            {
                Name = "stub_tool",
                Description = "Stub",
                InputSchema = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }),
                Handler = (_, _) => Task.FromResult<object?>(null)
            };
        }
    }
}
