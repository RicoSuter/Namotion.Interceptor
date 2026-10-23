using System.Net;
using System.Text;
using HomeBlaze.AI.Agents;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.E2E.Tests;

[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class AgentMcpRouteTests
{
    private readonly PlaywrightFixture _fixture;

    public AgentMcpRouteTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenTheAgentRouteIsRequested_ThenOnlyTheLoopbackListenerServesIt()
    {
        // Arrange
        var agentEndpoint = _fixture.ServerServices.GetRequiredService<IAgentEndpointAddress>().GetAgentEndpoint();
        using var client = new HttpClient();

        // Act
        using var mainServerResponse = await client.PostAsync(
            new Uri(new Uri(_fixture.ServerAddress), agentEndpoint!.AbsolutePath), new StringContent("{}", Encoding.UTF8, "application/json"));
        using var listenerResponse = await client.PostAsync(agentEndpoint, new StringContent("{}", Encoding.UTF8, "application/json"));

        // Assert
        // The catch-all page route answers a POST with 405; a served agent route would answer 401.
        Assert.Contains(mainServerResponse.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Equal(HttpStatusCode.Unauthorized, listenerResponse.StatusCode);
        Assert.Equal("127.0.0.1", agentEndpoint.Host);
        Assert.NotEqual(new Uri(_fixture.ServerAddress).Port, agentEndpoint.Port);
    }
}
