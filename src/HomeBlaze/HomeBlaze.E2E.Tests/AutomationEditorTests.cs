using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the edit dialog of an automation: each edit is saved to the file of the automation and shown again when
/// the dialog opens the next time.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class AutomationEditorTests
{
    private const int Timeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public AutomationEditorTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenTheNameIsChangedAndSaved_ThenTheFileHasItAndTheEditorShowsItAgain()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);

            // Act
            await dialog.GetByLabel("Name", new() { Exact = true }).FillAsync("Washing machine");
            await SaveAsync(page, dialog);

            // Assert
            Assert.Contains("\"name\": \"Washing machine\"", await _fixture.ReadFileAsync(filePath));
            await Assertions.Expect(page.GetByTestId("automation-page").GetByRole(AriaRole.Heading, new() { Name = "Washing machine" }))
                .ToBeVisibleAsync(new() { Timeout = Timeout });
            var (_, reopened) = await OpenEditorAsync(name, page);
            await Assertions.Expect(reopened.GetByLabel("Name", new() { Exact = true })).ToHaveValueAsync("Washing machine");
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheAutomationIsDisabledAndSaved_ThenTheFileHasItAndTheRunningAutomationStops()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await Assertions.Expect(page.GetByTestId("automation-status")).ToHaveTextAsync("Running", new() { Timeout = Timeout });

            // Act
            await dialog.GetByLabel("Enabled", new() { Exact = false }).ClickAsync();
            await SaveAsync(page, dialog);

            // Assert
            Assert.Contains("\"enabled\": false", await _fixture.ReadFileAsync(filePath));
            await Assertions.Expect(page.GetByTestId("automation-status")).ToHaveTextAsync("Stopped", new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheConfigurationIsInvalid_ThenTheDialogListsTheErrorsAndSaveIsDisabled()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.InitialState = "Nowhere";
        });
        try
        {
            // Act
            var (_, dialog) = await OpenEditorAsync(name);

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToContainTextAsync("Nowhere", new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true })).ToBeDisabledAsync();
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    /// <summary>Opens the page of the automation, or uses the open one, and opens its edit dialog with its Edit button.</summary>
    private async Task<(IPage Page, ILocator Dialog)> OpenEditorAsync(string name, IPage? page = null)
    {
        if (page is null)
        {
            page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{name}");
            await page.WaitUntilInteractiveAsync();
        }

        await page.GetByTestId("automation-edit").ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog.GetByTestId("automation-editor")).ToBeVisibleAsync(new() { Timeout = Timeout });
        return (page, dialog);
    }

    /// <summary>Saves the dialog and waits for the notification, which is the last step of a save, and for the dialog to close.</summary>
    private static async Task SaveAsync(IPage page, ILocator dialog)
    {
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(dialog).ToHaveCountAsync(0);
    }
}
