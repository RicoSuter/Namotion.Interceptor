using System.Net;
using HomeBlaze.E2E.Tests.Infrastructure;

namespace HomeBlaze.E2E.Tests;

[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class HealthEndpointTests
{
    private readonly PlaywrightFixture _fixture;

    public HealthEndpointTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task WhenHealthEndpointIsRequested_ThenItReturnsOk(string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = new Uri(_fixture.ServerAddress) };

        // Act
        using var response = await client.GetAsync(path);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
