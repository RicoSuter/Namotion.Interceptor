using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;

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
            await dialog.GetByTestId("automation-enabled").ClickAsync();
            await SaveAsync(page, dialog);

            // Assert
            Assert.Contains("\"enabled\": false", await _fixture.ReadFileAsync(filePath));
            // The worker sets both after it applied the configuration, which the host requests after the write, so they
            // prove that the running automation took the new configuration.
            await Assertions.Expect(page.GetByTestId("automation-status")).ToHaveTextAsync("Stopped", new() { Timeout = Timeout });
            await Assertions.Expect(page.GetByTestId("automation-rule-row").First).ToContainTextAsync("the automation is disabled", new() { Timeout = Timeout });
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

    [Fact]
    public async Task WhenTheDialogIsOpened_ThenItIsNotDirtyUntilSomethingIsEdited()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            // Act
            var (_, dialog) = await OpenEditorAsync(name);
            var editor = dialog.GetByTestId("automation-editor");

            // Assert
            await Assertions.Expect(editor).ToHaveAttributeAsync("data-dirty", "false");
            await dialog.GetByLabel("Name", new() { Exact = true }).FillAsync("Washing machine");
            await Assertions.Expect(editor).ToHaveAttributeAsync("data-dirty", "true", new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheDialogIsCanceledAfterAnEdit_ThenTheAutomationAndItsFileAreUnchanged()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            var before = await _fixture.ReadFileAsync(filePath);
            await dialog.GetByLabel("Name", new() { Exact = true }).FillAsync("Washing machine");
            await dialog.GetByTestId("automation-enabled").ClickAsync();

            // Act
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(dialog).ToHaveCountAsync(0);

            // Assert
            await Assertions.Expect(page.GetByTestId("automation-page").GetByRole(AriaRole.Heading, new() { Name = "Washer" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("automation-status")).ToHaveTextAsync("Running");
            Assert.Equal(before, await _fixture.ReadFileAsync(filePath));
            var (_, reopened) = await OpenEditorAsync(name, page);
            await Assertions.Expect(reopened.GetByLabel("Name", new() { Exact = true })).ToHaveValueAsync("Washer");
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenAnAutomationIsCreatedInTheCreateDialog_ThenItsFileExistsAndItsPageOpens()
    {
        // Arrange
        var name = $"Created{Guid.NewGuid():N}";
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}browser");
        await page.WaitUntilInteractiveAsync();
        try
        {
            // Act
            await page.Locator("#scrollContainer button:has-text('Create')").First.ClickAsync();
            await SubjectSetupDialogTests.PickTypeAsync(page, "automation");
            await page.GetByTestId("create-name-input").FillAsync(name);
            var dialog = SubjectSetupDialogTests.Dialog(page);
            await Assertions.Expect(dialog.GetByTestId("automation-editor")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await dialog.GetByLabel("Name", new() { Exact = true }).FillAsync("Created by test");
            await Assertions.Expect(SubjectSetupDialogTests.CreateButton(page)).ToBeEnabledAsync(new() { Timeout = Timeout });
            await SubjectSetupDialogTests.CreateButton(page).ClickAsync();
            await Assertions.Expect(dialog).Not.ToBeVisibleAsync(new() { Timeout = Timeout });

            // Assert
            var filePath = _fixture.GetPagePath($"{name}.json");
            await AsyncTestHelpers.WaitUntilAsync(() => File.Exists(filePath), timeout: TimeSpan.FromSeconds(30), message: "The automation file was not written");
            Assert.Contains("\"name\": \"Created by test\"", await _fixture.ReadFileAsync(filePath));
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{name}");
            await Assertions.Expect(page.GetByTestId("automation-page").GetByRole(AriaRole.Heading, new() { Name = "Created by test" }))
                .ToBeVisibleAsync(new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.DeleteStoredSubjectByNameAsync(name);
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
