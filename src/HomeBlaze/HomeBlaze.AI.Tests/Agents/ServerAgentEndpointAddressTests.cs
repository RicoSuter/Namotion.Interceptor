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
    [InlineData(new[] { "http://192.168.1.5:5192", "http://localhost:5193" }, "http://127.0.0.1:5193/mcp/agents")]
    [InlineData(new[] { "http://192.168.1.5:5192", "https://localhost:7298" }, "http://192.168.1.5:5192/mcp/agents")]
    [InlineData(new[] { "https://192.168.1.5:7299", "https://localhost:7298" }, "https://127.0.0.1:7298/mcp/agents")]
    [InlineData(new[] { "https://192.168.1.5:7299" }, "https://192.168.1.5:7299/mcp/agents")]
    [InlineData(new[] { "http://192.168.1.5:5192" }, "http://192.168.1.5:5192/mcp/agents")]
    [InlineData(new[] { "http://unix:/tmp/homeblaze.sock", "http://192.168.1.5:5192" }, "http://192.168.1.5:5192/mcp/agents")]
    public void WhenResolvingFromServerAddresses_ThenHttpIsPreferredOverHttpsAndLoopbackOverOtherAddresses(string[] addresses, string expected)
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

    [Theory]
    [InlineData("http://unix:/tmp/server.sock")]
    [InlineData("http://pipe:/server")]
    public void WhenTheServerHasOnlyNonIpAddresses_ThenNoEndpointIsResolved(string address)
    {
        // Act
        var endpoint = ServerAgentEndpointAddress.Resolve([address]);

        // Assert
        Assert.Null(endpoint);
    }
}
