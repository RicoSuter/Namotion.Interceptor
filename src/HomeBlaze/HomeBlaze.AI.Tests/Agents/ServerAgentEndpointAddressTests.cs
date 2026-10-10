using HomeBlaze.AI.Agents;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class ServerAgentEndpointAddressTests
{
    [Theory]
    [InlineData(new[] { "http://localhost:5192" }, "http://127.0.0.1:5192/mcp/agents")]
    [InlineData(new[] { "https://localhost:7298", "http://localhost:5192" }, "http://127.0.0.1:5192/mcp/agents")]
    [InlineData(new[] { "http://[::]:8080" }, "http://127.0.0.1:8080/mcp/agents")]
    [InlineData(new[] { "http://0.0.0.0:8080" }, "http://127.0.0.1:8080/mcp/agents")]
    [InlineData(new[] { "http://+:8080" }, "http://127.0.0.1:8080/mcp/agents")]
    [InlineData(new[] { "https://localhost:7298" }, "https://127.0.0.1:7298/mcp/agents")]
    public void WhenResolvingFromServerAddresses_ThenALoopbackHttpAddressIsPreferred(string[] addresses, string expected)
    {
        // Act
        var endpoint = ServerAgentEndpointAddress.Resolve(addresses);

        // Assert
        Assert.Equal(expected, endpoint?.ToString());
    }

    [Fact]
    public void WhenTheServerHasNoAddresses_ThenNoEndpointIsResolved()
    {
        // Act
        var endpoint = ServerAgentEndpointAddress.Resolve([]);

        // Assert
        Assert.Null(endpoint);
    }
}
