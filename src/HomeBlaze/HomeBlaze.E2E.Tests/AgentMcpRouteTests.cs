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
        // Without following redirects, so that a challenge of the main server is seen as such.
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var mainServerUrl = new Uri(new Uri(_fixture.ServerAddress), agentEndpoint!.AbsolutePath);
        var adminPage = await _fixture.CreatePageAsync(SignedInAs.Admin);

        // Act
        using var mainServerResponse = await client.PostAsync(
            mainServerUrl, new StringContent("{}", Encoding.UTF8, "application/json"));
        // With the cookies of the signed in administrator, so that only a route the main server does not serve is left to answer.
        var adminResponse = await adminPage.APIRequest.PostAsync(mainServerUrl.ToString(), new()
        {
            Data = "{}",
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            MaxRedirects = 0
        });
        using var listenerResponse = await client.PostAsync(agentEndpoint, new StringContent("{}", Encoding.UTF8, "application/json"));

        // Assert
        // The fallback policy sends any request that is not signed in to sign in, served route or not, so this only shows the
        // request is unauthenticated. The signed in administrator is not redirected, and the main server has no agent route to answer.
        Assert.Equal(HttpStatusCode.Redirect, mainServerResponse.StatusCode);
        Assert.Equal("/", new Uri(new Uri(_fixture.ServerAddress), mainServerResponse.Headers.Location!).AbsolutePath);
        Assert.NotEqual((int)HttpStatusCode.Redirect, adminResponse.Status);
        Assert.Contains(adminResponse.Status, new[] { 404, 405 });
        Assert.Equal(HttpStatusCode.Unauthorized, listenerResponse.StatusCode);
        Assert.Equal("127.0.0.1", agentEndpoint.Host);
        Assert.NotEqual(new Uri(_fixture.ServerAddress).Port, agentEndpoint.Port);
    }
}
