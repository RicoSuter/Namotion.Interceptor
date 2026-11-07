using Bunit;
using HomeBlaze.Authentication.Blazor.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public sealed class UnencryptedHttpWarningTests : AccountComponentsTestContext
{
    [Theory]
    [InlineData("https://example.com/", true, false)]
    [InlineData("https://example.com/", false, true)]
    [InlineData("http://example.com/", true, true)]
    [InlineData("http://example.com/", false, true)]
    public async Task WhenTheBrowserAndTheServerSawHttps_ThenTheWarningShowsOtherwise(string browserUrl, bool serverSawHttps, bool expectsWarning)
    {
        // Arrange
        await UseGraphAsync(roles: null);
        Services.GetRequiredService<NavigationManager>().NavigateTo(browserUrl);
        Services.GetRequiredService<RequestTransport>().ServerSawHttps = serverSawHttps;

        // Act
        var warning = Render<UnencryptedHttpWarning>();

        // Assert
        Assert.Equal(expectsWarning, warning.FindAll("[data-testid=unencrypted-http-warning]").Count == 1);
    }

    [Fact]
    public async Task WhenNothingIsKnownOfTheRequest_ThenTheWarningShowsOverHttps()
    {
        // Arrange
        await UseGraphAsync(roles: null);
        Services.GetRequiredService<NavigationManager>().NavigateTo("https://example.com/");

        // Act
        var warning = Render<UnencryptedHttpWarning>();

        // Assert
        Assert.Single(warning.FindAll("[data-testid=unencrypted-http-warning]"));
    }
}
