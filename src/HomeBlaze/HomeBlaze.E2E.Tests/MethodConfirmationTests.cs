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
    private const int ElementVisibilityTimeout = 15000;

    private readonly PlaywrightFixture _fixture;

    public MethodConfirmationTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAnOperationWithoutParametersRequiresConfirmation_ThenTheConfirmationDialogAsksBeforeItRuns()
    {
        // Arrange
        var page = await SubjectBrowserPage.OpenAsync(_fixture, "Agents/TestAgent");

        // Act
        await page.GetByRole(AriaRole.Button, new() { Name = "New Conversation" }).ClickAsync();

        // Assert
        var dialog = page.GetByRole(AriaRole.Dialog).Filter(new() { HasText = "Are you sure you want to execute this operation?" });
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = ElementVisibilityTimeout });
    }
}
