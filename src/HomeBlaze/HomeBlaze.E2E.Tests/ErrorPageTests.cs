using System.Net;
using HomeBlaze.E2E.Tests.Infrastructure;

namespace HomeBlaze.E2E.Tests;

[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class ErrorPageTests
{
    private readonly PlaywrightFixture _fixture;

    public ErrorPageTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAVisitorWhoIsNotSignedInAndHasNoRolesRequestsTheErrorPage_ThenItShowsWithoutAnyPageInTheNavigation()
    {
        // Arrange
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(_fixture.ServerAddress) };

        // Act
        using var response = await client.GetAsync("/Error");

        // Assert
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("An error occurred while processing your request.", html);
        Assert.Contains("id=\"nav-drawer\"", html);
        Assert.DoesNotContain("href=\"pages/", html);
        Assert.DoesNotContain("mud-nav-group", html);
    }
}
