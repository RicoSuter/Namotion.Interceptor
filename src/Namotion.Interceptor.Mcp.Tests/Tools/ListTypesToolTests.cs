using System.Text.Json;
using Namotion.Interceptor.Mcp.Implementations;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class ListTypesToolTests
{
    [Fact]
    public async Task ListTypes_returns_types_from_all_providers()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context);

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            TypeProviders = { new SubjectAbstractionsAssemblyTypeProvider() }
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "list_types");

        // Act
        var input = JsonSerializer.SerializeToElement(new { });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var types = json.GetProperty("types");
        Assert.True(types.GetArrayLength() > 0);
    }
}
