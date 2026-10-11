using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for operations that ask a person before they run.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class MethodConfirmationTests
{
    private const int PageLoadTimeout = 30000;
    private const int ElementVisibilityTimeout = 15000;

    // The page is prerendered, and a click before its circuit is interactive is lost. The pane width is set by the
    // script the interactive browser runs, so it marks the point from which clicks arrive.
    private const string BrowserIsInteractive =
        "() => (document.getElementById('scrollContainer')?.style.getPropertyValue('--pane-width') ?? '') !== ''";

    private readonly PlaywrightFixture _fixture;

    public MethodConfirmationTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAddingAPlugin_ThenTheParameterDialogAsksBeforeAnythingRuns()
    {
        // Arrange
        var page = await OpenInBrowserAsync("Plugins");

        // Act
        await page.GetByRole(AriaRole.Button, new() { Name = "Add Plugin" }).ClickAsync();

        // Assert
        var dialog = page.GetByRole(AriaRole.Dialog).Filter(new() { HasText = "Add Plugin" });
        await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Execute" })).ToBeVisibleAsync(
            new() { Timeout = ElementVisibilityTimeout });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenAnOperationWithoutParametersRequiresConfirmation_ThenTheConfirmationDialogAsksBeforeItRuns()
    {
        // Arrange
        var page = await OpenInBrowserAsync("Agents/TestAgent");

        // Act
        await page.GetByRole(AriaRole.Button, new() { Name = "New Conversation" }).ClickAsync();

        // Assert
        var dialog = page.GetByRole(AriaRole.Dialog).Filter(new() { HasText = "Are you sure you want to execute this operation?" });
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    private async Task<IPage> OpenInBrowserAsync(string path)
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}browser/{path}");
        await page.WaitForFunctionAsync(BrowserIsInteractive, null, new() { Timeout = PageLoadTimeout });
        return page;
    }
}
