using HomeBlaze.E2E.Tests.Infrastructure;
using System.Text.Json;
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
    public async Task WhenARuleIsRenamedAndItsConditionAndHoldAreChanged_ThenTheyAreSavedAndShownAgain()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await dialog.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Finished" }).ClickAsync();
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Finished", new() { Timeout = Timeout });

            // Act
            await dialog.GetByLabel("Rule name").FillAsync("Done");
            await dialog.GetByTestId("automation-trigger-condition-advanced").ClickAsync();
            await dialog.GetByTestId("automation-trigger-condition-text").FillAsync("/Demo/Motor/Temperature == 3");
            await dialog.GetByLabel("For at least").FillAsync("5m");
            await SaveAsync(page, dialog);

            // Assert
            var json = await _fixture.ReadFileAsync(filePath);
            Assert.Contains("\"name\": \"Done\"", json);
            Assert.Contains("/Demo/Motor/Temperature == 3", json);
            Assert.Contains("\"for\": \"00:05:00\"", json);
            var (_, reopened) = await OpenEditorAsync(name, page);
            await reopened.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await reopened.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Done" }).ClickAsync();
            await Assertions.Expect(reopened.GetByLabel("For at least")).ToHaveValueAsync("5m");
            await Assertions.Expect(reopened.GetByTestId("automation-trigger-condition-row")).ToHaveCountAsync(1);
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenAHoldIsNoSingleDuration_ThenItShowsAnErrorAndSaveIsDisabledUntilItIsFixed()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await dialog.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Finished" }).ClickAsync();
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Finished", new() { Timeout = Timeout });
            var save = dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });

            // Act
            await dialog.GetByLabel("For at least").FillAsync("20s + 1m");

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToContainTextAsync("For at least", new() { Timeout = Timeout });
            await Assertions.Expect(save).ToBeDisabledAsync();
            await dialog.GetByLabel("For at least").FillAsync("1m");
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await Assertions.Expect(save).ToBeEnabledAsync();
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenARuleWithAnUnfinishedConditionIsDeleted_ThenTheEditorNoLongerBlocksSaving()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await dialog.GetByTestId("automation-add-rule").ClickAsync();
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Rule 3", new() { Timeout = Timeout });
            await dialog.GetByTestId("automation-trigger-condition-add-row").ClickAsync();
            await dialog.GetByLabel("Value", new() { Exact = true }).FillAsync("1,5");
            await dialog.GetByLabel("Value", new() { Exact = true }).PressAsync("Tab");
            await Assertions.Expect(dialog.GetByTestId("automation-pending")).ToBeVisibleAsync(new() { Timeout = Timeout });

            // Act
            await dialog.GetByTestId("automation-delete-rule").ClickAsync();

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-pending")).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await SaveAsync(page, dialog);
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenAPropertyConditionWithAnUnfinishedRowIsRemoved_ThenTheEditorNoLongerBlocksSaving()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await dialog.GetByTestId("automation-add-property-trigger").ClickAsync();
            await Assertions.Expect(dialog.GetByTestId("automation-property-trigger")).ToHaveCountAsync(2, new() { Timeout = Timeout });
            var added = dialog.GetByTestId("automation-property-trigger").Last;
            await added.GetByTestId("automation-trigger-condition-add-row").ClickAsync();
            await added.GetByLabel("Value", new() { Exact = true }).FillAsync("1,5");
            await added.GetByLabel("Value", new() { Exact = true }).PressAsync("Tab");
            await Assertions.Expect(dialog.GetByTestId("automation-pending")).ToBeVisibleAsync(new() { Timeout = Timeout });

            // Act
            await added.GetByRole(AriaRole.Button, new() { Name = "Remove trigger" }).ClickAsync();

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-pending")).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await SaveAsync(page, dialog);
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenARuleWithAGuardAndACooldownIsOpenedAndSaved_ThenItsGuardTextIsUnchanged()
    {
        // Arrange
        const string guard = "(/Demo/Motor/Temperature > 1) and (./LastTriggeredAt == null or now - ./LastTriggeredAt > 30m)";
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Rules["started"].Guard = guard;
        });
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();

            // Act
            await Assertions.Expect(dialog.GetByLabel("At most once every")).ToHaveValueAsync("30m");
            await SaveAsync(page, dialog);

            // Assert
            using var saved = JsonDocument.Parse(await _fixture.ReadFileAsync(filePath));
            Assert.Equal(guard, saved.RootElement.GetProperty("rules").GetProperty("started").GetProperty("guard").GetString());
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenARuleIsAdded_ThenItStartsInDryRunWithOneEmptyConditionAndNeedsAnActionBeforeItCanBeSaved()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();

            // Act
            await dialog.GetByTestId("automation-add-rule").ClickAsync();

            // Assert
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Rule 3", new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Rule 3" }))
                .ToContainTextAsync("Dry run, 1 trigger");
            await Assertions.Expect(dialog.GetByTestId("automation-property-trigger")).ToHaveCountAsync(1);
            await Assertions.Expect(dialog.GetByTestId("automation-rule-error").First).ToBeVisibleAsync();
            await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true })).ToBeDisabledAsync();
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheRulesTabIsOpenedAndRulesAreSelected_ThenTheDialogStaysClean()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);

            // Act
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await dialog.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Finished" }).ClickAsync();
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Finished", new() { Timeout = Timeout });
            await dialog.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Started" }).ClickAsync();
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Started", new() { Timeout = Timeout });

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-editor")).ToHaveAttributeAsync("data-dirty", "false");
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheMoveButtonOfARuleIsClicked_ThenTheSelectedRuleStays()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Started", new() { Timeout = Timeout });

            // Act
            await dialog.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Finished" })
                .GetByRole(AriaRole.Button, new() { Name = "Move rule up" }).ClickAsync();

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-rule-item").First).ToContainTextAsync("Finished", new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Started");
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheCooldownIsBeingTypedOrIsZero_ThenAnErrorShowsOnlyForWhatCannotBecomeValid()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            var cooldown = dialog.GetByLabel("At most once every");
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Started", new() { Timeout = Timeout });

            // Act
            await cooldown.PressSequentiallyAsync("30");

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToHaveCountAsync(0);
            await cooldown.FillAsync("0s");
            await Assertions.Expect(dialog.GetByTestId("automation-guard")).ToContainTextAsync("Use a duration above 0", new() { Timeout = Timeout });
            await Assertions.Expect(cooldown).ToHaveValueAsync("0s");
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenAParenthesisIsTypedIntoTheGuardOfARuleWithACooldown_ThenTheCooldownIsKept()
    {
        // Arrange
        const string condition = "(/Demo/Motor/Temperature > 1 or /Demo/Motor/Temperature < 0)";
        const string cooldown = "./LastTriggeredAt == null or now - ./LastTriggeredAt > 30m";
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Rules["started"].Guard = cooldown;
        });
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await Assertions.Expect(dialog.GetByLabel("At most once every")).ToHaveValueAsync("30m", new() { Timeout = Timeout });

            // Act
            await dialog.GetByTestId("automation-guard-condition-advanced").ClickAsync();
            await dialog.GetByTestId("automation-guard-condition-text").PressSequentiallyAsync(condition);
            await Assertions.Expect(dialog.GetByTestId("automation-guard-condition-text")).ToHaveValueAsync(condition);
            await SaveAsync(page, dialog);

            // Assert
            using var saved = JsonDocument.Parse(await _fixture.ReadFileAsync(filePath));
            var guard = saved.RootElement.GetProperty("rules").GetProperty("started").GetProperty("guard").GetString();
            Assert.Equal($"{condition} and ({cooldown})", guard);
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenACooldownIsSetOnARule_ThenItIsSavedAsTheGuardOfTheRule()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();

            // Act
            await dialog.GetByLabel("At most once every").FillAsync("1h30m");
            await SaveAsync(page, dialog);

            // Assert
            using var saved = JsonDocument.Parse(await _fixture.ReadFileAsync(filePath));
            var guard = saved.RootElement.GetProperty("rules").GetProperty("started").GetProperty("guard").GetString();
            Assert.Equal("./LastTriggeredAt == null or now - ./LastTriggeredAt > 1h30m", guard);
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
