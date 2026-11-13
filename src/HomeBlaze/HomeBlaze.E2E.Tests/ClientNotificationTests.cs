using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Installation;
using HomeBlaze.Services.Clients;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests;

[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class ClientNotificationTests
{
    private const int Timeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public ClientNotificationTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenABrowserIsNamedAndNotified_ThenItShowsTheNotificationDialog()
    {
        // Arrange
        var (name, installation) = await _fixture.AddSubjectToRootAsync<HomeBlazeInstallation>();
        var clientName = $"E2E browser {Guid.NewGuid():N}";
        try
        {
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Dashboard.md");
            await page.WaitUntilInteractiveAsync(Timeout);
            await page.OpenAppBarMenuAsync();
            await page.GetByTestId("app-bar-menu-name-browser").ClickAsync();
            await page.GetByTestId("client-name-input").FillAsync(clientName);
            await page.GetByTestId("client-name-save").ClickAsync();

            await AsyncTestHelpers.WaitUntilAsync(
                () => installation.Clients.Values.Any(client => client.Name == clientName && client.IsConnected),
                message: "The browser was not named");
            var client = installation.Clients.Values.Single(client => client.Name == clientName);

            // Act
            await client.SendNotificationAsync(new Notification { Title = "E2E", Message = "Hello from the test" }, CancellationToken.None);

            // Assert
            await Assertions.Expect(page.GetByTestId("client-notification-message"))
                .ToHaveTextAsync("Hello from the test", new() { Timeout = Timeout });
            var cookies = await page.Context.CookiesAsync();
            var tokenCookie = Assert.Single(cookies, cookie => cookie.Name == "homeblaze.client-token");
            Assert.NotEqual(client.Id, tokenCookie.Value);
            Assert.Equal(client.TokenHash, ClientConnectionService.HashClientToken(tokenCookie.Value));
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, installation);
        }
    }
}
