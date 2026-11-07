using HomeBlaze.Automation;
using HomeBlaze.E2E.Tests.Infrastructure;
using System.Text.Json;
using Microsoft.Playwright;
using Namotion.Interceptor;
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
                .ToContainTextAsync("Dry run, any state, 1 trigger");
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
    public async Task WhenAnEventTriggerAndAnActionAreAdded_ThenTheyAreSavedAndShownAgain()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await dialog.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Started" }).ClickAsync();
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Started", new() { Timeout = Timeout });

            // Act
            await dialog.GetByTestId("automation-add-event-trigger").ClickAsync();
            var eventTrigger = dialog.GetByTestId("automation-event-trigger");
            await eventTrigger.GetByTestId("automation-event-own").ClickAsync();
            await eventTrigger.GetByTestId("automation-event-filter-advanced").ClickAsync();
            await eventTrigger.GetByTestId("automation-event-filter-text").FillAsync("event/NewState == Running");
            await dialog.GetByTestId("automation-add-change-state").ClickAsync();
            await Assertions.Expect(dialog.GetByTestId("automation-action")).ToHaveCountAsync(2, new() { Timeout = Timeout });
            await SaveAsync(page, dialog);

            // Assert
            var json = await _fixture.ReadFileAsync(filePath);
            Assert.Contains("\"$type\": \"SubjectEventTrigger\"", json);
            Assert.Contains("\"subjectPath\": \"..\"", json);
            Assert.Contains("\"eventType\": \"AutomationStateChangedEvent\"", json);
            Assert.Contains("\"filter\": \"event/NewState == Running\"", json);
            Assert.Contains("\"id\": \"state-2\"", json);
            var (_, reopened) = await OpenEditorAsync(name, page);
            await reopened.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            var reopenedTrigger = reopened.GetByTestId("automation-event-trigger");
            await Assertions.Expect(reopenedTrigger).ToHaveCountAsync(1, new() { Timeout = Timeout });
            await Assertions.Expect(reopenedTrigger.GetByTestId("automation-event-source")).ToHaveTextAsync("This automation");
            await Assertions.Expect(reopenedTrigger.GetByTestId("automation-event-type")).ToHaveValueAsync("Automation state changed");
            await reopenedTrigger.GetByTestId("automation-event-filter-advanced").ClickAsync();
            await Assertions.Expect(reopenedTrigger.GetByTestId("automation-event-filter-text")).ToHaveValueAsync("event/NewState == Running");
            await Assertions.Expect(reopened.GetByTestId("automation-action")).ToHaveCountAsync(2);
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenARuleIsGivenAConditionAndAnActionAndSaved_ThenTheFileAndThePageHaveIt()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await dialog.GetByTestId("automation-add-rule").ClickAsync();
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Rule 3", new() { Timeout = Timeout });

            // Act
            await dialog.GetByTestId("automation-trigger-condition-advanced").ClickAsync();
            await dialog.GetByTestId("automation-trigger-condition-text").FillAsync("/Demo/Motor/Temperature > 2000");
            await dialog.GetByTestId("automation-add-change-state").ClickAsync();
            await Assertions.Expect(dialog.GetByTestId("automation-action")).ToHaveCountAsync(1, new() { Timeout = Timeout });
            await SaveAsync(page, dialog);

            // Assert
            using var saved = JsonDocument.Parse(await _fixture.ReadFileAsync(filePath));
            var rule = saved.RootElement.GetProperty("rules").EnumerateObject().Single(entry => entry.Value.GetProperty("name").GetString() == "Rule 3").Value;
            Assert.Equal("/Demo/Motor/Temperature > 2000", rule.GetProperty("triggers")[0].GetProperty("expression").GetString());
            Assert.Equal("state-1", rule.GetProperty("actions")[0].GetProperty("id").GetString());
            Assert.Equal("Idle", rule.GetProperty("actions")[0].GetProperty("targetState").GetString());
            await Assertions.Expect(page.GetByTestId("automation-rule-row")).ToHaveCountAsync(3, new() { Timeout = Timeout });
            await Assertions.Expect(page.GetByTestId("automation-page")).ToContainTextAsync("Rule 3", new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenACallOperationIsGivenItsArguments_ThenTheyAreSavedAndMissingOnesBlockSaving()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await dialog.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Started" }).ClickAsync();
            await dialog.GetByTestId("automation-add-call-operation").ClickAsync();
            var action = dialog.GetByTestId("automation-action").Last;

            // Act
            await action.GetByLabel("Call operation", new() { Exact = true }).FillAsync("/Demo/Motor/RunTest");
            await action.GetByLabel("Speed").FillAsync("50");

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToContainTextAsync("DurationSeconds", new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true })).ToBeDisabledAsync();
            await action.GetByLabel("DurationSeconds").FillAsync("5");
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await SaveAsync(page, dialog);
            using var saved = JsonDocument.Parse(await _fixture.ReadFileAsync(filePath));
            var call = saved.RootElement.GetProperty("rules").GetProperty("started").GetProperty("actions")[1];
            Assert.Equal("/Demo/Motor/RunTest", call.GetProperty("operationPath").GetString());
            Assert.Equal("50", call.GetProperty("arguments").GetProperty("speed").GetString());
            Assert.Equal("5", call.GetProperty("arguments").GetProperty("durationSeconds").GetString());
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheOperationOfAnActionAppearsWhileTheDialogIsOpen_ThenItsMissingArgumentIsListedAndSaveIsDisabled()
    {
        // Arrange
        var motorName = PlaywrightFixture.CreateMotorName();
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Rules["started"].Actions =
            [
                new InvokeOperationAction
                {
                    Id = "call-1",
                    OperationPath = $"/{motorName}/RunTest",
                    Arguments = new Dictionary<string, string> { ["speed"] = "50" }
                }
            ];
        });
        string? motorFilePath = null;
        IInterceptorSubject? motor = null;
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await Assertions.Expect(dialog.GetByTestId("automation-action")).ToContainTextAsync("No operation at this path.", new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true })).ToBeEnabledAsync();

            // Act
            (_, motorFilePath, motor) = await _fixture.AddStoredMotorAsync(motorName);

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-action").GetByLabel("DurationSeconds")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToContainTextAsync(
                "Rule 'Started': action 'call-1': DurationSeconds: Required", new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true })).ToBeDisabledAsync();
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
            if (motorFilePath is not null && motor is not null)
            {
                await _fixture.DeleteStoredSubjectAsync(motorFilePath, motor);
            }
        }
    }

    [Fact]
    public async Task WhenTheOperationOfAnActionInARuleThatIsNotShownAppearsBeforeSave_ThenSaveListsItsMissingArgumentAndKeepsTheDialogOpen()
    {
        // Arrange
        var motorName = PlaywrightFixture.CreateMotorName();
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Rules["finished"].Actions =
            [
                new InvokeOperationAction
                {
                    Id = "call-1",
                    OperationPath = $"/{motorName}/RunTest",
                    Arguments = new Dictionary<string, string> { ["speed"] = "50" }
                }
            ];
        });
        string? motorFilePath = null;
        IInterceptorSubject? motor = null;
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByLabel("Name", new() { Exact = true }).FillAsync("Changed washer");
            var save = dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });
            await Assertions.Expect(save).ToBeEnabledAsync(new() { Timeout = Timeout });
            var fileBefore = await _fixture.ReadFileAsync(filePath);
            (_, motorFilePath, motor) = await _fixture.AddStoredMotorAsync(motorName);

            // Act
            await save.ClickAsync();

            // Assert
            await Assertions.Expect(page.GetByText("Fix the errors listed in the editor before saving.")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToContainTextAsync(
                "Rule 'Finished': action 'call-1': DurationSeconds: Required", new() { Timeout = Timeout });
            await Assertions.Expect(save).ToBeDisabledAsync();
            await Assertions.Expect(dialog.GetByTestId("automation-editor")).ToBeVisibleAsync();
            Assert.Equal(fileBefore, await _fixture.ReadFileAsync(filePath));
            Assert.Equal("Washer", automation.Name);
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
            if (motorFilePath is not null && motor is not null)
            {
                await _fixture.DeleteStoredSubjectAsync(motorFilePath, motor);
            }
        }
    }

    [Fact]
    public async Task WhenAValueThatATemplateReadsChangesWhileTheDialogIsOpen_ThenTheTextShownFollowsIt()
    {
        // Arrange
        var (motorName, motorFilePath, motor) = await _fixture.AddStoredMotorAsync();
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Rules["started"].Actions =
            [
                new SetPropertyAction { Id = "write-1", PropertyPath = $"/{motorName}/Name", Value = $"Speed {{{{ /{motorName}/TargetSpeed }}}}" }
            ];
        });
        try
        {
            ((HomeBlaze.Samples.Motor)motor).TargetSpeed = 100;
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            var template = dialog.GetByTestId("automation-template-field");
            await Assertions.Expect(template).ToContainTextAsync("Now: Speed 100", new() { Timeout = Timeout });

            // Act
            ((HomeBlaze.Samples.Motor)motor).TargetSpeed = 200;

            // Assert
            await Assertions.Expect(template).ToContainTextAsync("Now: Speed 200", new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
            await _fixture.DeleteStoredSubjectAsync(motorFilePath, motor);
        }
    }

    [Fact]
    public async Task WhenTheOperationPathIsEditedCharacterByCharacter_ThenTheArgumentsAreKept()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Rules["started"].Actions =
            [
                new InvokeOperationAction
                {
                    Id = "call-1",
                    OperationPath = "/Demo/Motor/RunTest",
                    Arguments = new Dictionary<string, string> { ["speed"] = "50", ["durationSeconds"] = "5" }
                }
            ];
        });
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            var action = dialog.GetByTestId("automation-action");
            var path = action.GetByLabel("Call operation", new() { Exact = true });
            await Assertions.Expect(action.GetByLabel("Speed")).ToHaveValueAsync("50", new() { Timeout = Timeout });

            // Act
            await path.ClickAsync();
            await path.PressAsync("End");
            await path.PressAsync("Backspace");
            await Assertions.Expect(action.GetByLabel("Speed")).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await path.PressSequentiallyAsync("t");

            // Assert
            await Assertions.Expect(action.GetByLabel("Speed")).ToHaveValueAsync("50", new() { Timeout = Timeout });
            await Assertions.Expect(action.GetByLabel("DurationSeconds")).ToHaveValueAsync("5");
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenActionsAreMovedAndRemoved_ThenTheFileHasThemInTheNewOrder()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await dialog.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Started" }).ClickAsync();
            await dialog.GetByTestId("automation-add-change-state").ClickAsync();
            await dialog.GetByTestId("automation-add-change-state").ClickAsync();
            var actions = dialog.GetByTestId("automation-action");
            await Assertions.Expect(actions).ToHaveCountAsync(3, new() { Timeout = Timeout });

            // Act
            await actions.Nth(2).GetByRole(AriaRole.Button, new() { Name = "Move action up" }).ClickAsync();
            await actions.Nth(0).GetByRole(AriaRole.Button, new() { Name = "Remove action" }).ClickAsync();
            await Assertions.Expect(actions).ToHaveCountAsync(2, new() { Timeout = Timeout });
            await SaveAsync(page, dialog);

            // Assert
            using var saved = JsonDocument.Parse(await _fixture.ReadFileAsync(filePath));
            var ids = saved.RootElement.GetProperty("rules").GetProperty("started").GetProperty("actions").EnumerateArray()
                .Select(action => action.GetProperty("id").GetString()!).ToArray();
            Assert.Equal(["state-3", "state-2"], ids);
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenAnArgumentIsMissingAndAnotherRuleIsSelected_ThenSaveStaysDisabled()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await dialog.GetByTestId("automation-add-call-operation").ClickAsync();
            var action = dialog.GetByTestId("automation-action").Last;
            await action.GetByLabel("Call operation", new() { Exact = true }).FillAsync("/Demo/Motor/RunTest");
            await action.GetByLabel("Speed").FillAsync("50");
            var save = dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });
            await Assertions.Expect(save).ToBeDisabledAsync();

            // Act
            await dialog.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Finished" }).ClickAsync();
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Finished", new() { Timeout = Timeout });
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "General" }).ClickAsync();

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToContainTextAsync("action 'call-1': DurationSeconds: Required", new() { Timeout = Timeout });
            await Assertions.Expect(save).ToBeDisabledAsync();
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenAStoredActionLacksARequiredArgument_ThenSaveIsDisabledWithTheReasonListed()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Rules["finished"].Actions =
            [
                new InvokeOperationAction
                {
                    Id = "call-1",
                    OperationPath = "/Demo/Motor/RunTest",
                    Arguments = new Dictionary<string, string> { ["speed"] = "50" }
                }
            ];
        });
        try
        {
            // Act
            var (_, dialog) = await OpenEditorAsync(name);

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToContainTextAsync("Rule 'Finished': action 'call-1': DurationSeconds: Required", new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true })).ToBeDisabledAsync();
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheLastEventTriggerIsRemovedWhileAnArgumentReadsTheEvent_ThenTheErrorIsListedAtOnce()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Rules["started"].Triggers =
            [
                new SubjectEventTrigger { SubjectPath = "..", EventType = "AutomationStateChangedEvent" },
                new PropertyTrigger { Expression = "/Demo/Motor/Temperature > 1000" }
            ];
            configured.Rules["started"].Actions =
            [
                new SetPropertyAction { Id = "set-1", PropertyPath = "/Demo/Motor/CurrentSpeed", Value = "event/Timestamp" }
            ];
        });
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await Assertions.Expect(dialog.GetByTestId("automation-event-trigger")).ToHaveCountAsync(1, new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToHaveCountAsync(0);

            // Act
            await dialog.GetByTestId("automation-event-trigger").GetByRole(AriaRole.Button, new() { Name = "Remove trigger" }).ClickAsync();

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToContainTextAsync("action 'set-1': Value", new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenOnlyTheOperationOfOneActionIsChanged_ThenOnlyThatActionLosesArgumentsTheNewOperationDoesNotTake()
    {
        // Arrange
        var arguments = new Dictionary<string, string> { ["speed"] = "50", ["durationSeconds"] = "5", ["gone"] = "x" };
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Rules["started"].Actions =
            [
                new InvokeOperationAction { Id = "call-1", OperationPath = "/Demo/Motor/RunTest", Arguments = arguments },
                new InvokeOperationAction { Id = "call-2", OperationPath = "/Demo/Motor/RunTest", Arguments = arguments }
            ];
        });
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            var second = dialog.GetByTestId("automation-action").Nth(1);
            await Assertions.Expect(second.GetByLabel("Speed")).ToHaveValueAsync("50", new() { Timeout = Timeout });

            // Act
            await second.GetByLabel("Call operation", new() { Exact = true }).FillAsync("/Demo/Motor/SetTargetSpeed");
            await Assertions.Expect(second.GetByLabel("DurationSeconds")).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await SaveAsync(page, dialog);

            // Assert
            using var saved = JsonDocument.Parse(await _fixture.ReadFileAsync(filePath));
            var calls = saved.RootElement.GetProperty("rules").GetProperty("started").GetProperty("actions").EnumerateArray()
                .ToDictionary(action => action.GetProperty("id").GetString()!, action => action.GetProperty("arguments").EnumerateObject().Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(["durationSeconds", "gone", "speed"], calls["call-1"]);
            Assert.Equal(["speed"], calls["call-2"]);
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

    [Fact]
    public async Task WhenAStateIsRenamedAndAnotherIsAddedInTheDiagram_ThenTheRulesFollowAndTheChangeIsSavedAndShownAgain()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "States" }).ClickAsync();

            // Act
            await dialog.GetByTestId("automation-state-node-Running").ClickAsync();
            var stateName = dialog.GetByLabel("State name");
            await stateName.FillAsync("Washing");
            await stateName.PressAsync("Tab");
            await Assertions.Expect(dialog.GetByTestId("automation-state-node-Washing")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await dialog.GetByTestId("automation-add-state").ClickAsync();
            var addedName = await dialog.GetByLabel("State name").InputValueAsync();
            await Assertions.Expect(dialog.GetByTestId($"automation-state-node-{addedName}")).ToBeVisibleAsync();
            await SaveAsync(page, dialog);

            // Assert
            var json = await _fixture.ReadFileAsync(filePath);
            Assert.Contains("\"inState\": \"Washing\"", json);
            Assert.Contains("\"targetState\": \"Washing\"", json);
            Assert.Contains($"\"{addedName}\"", json);
            Assert.DoesNotContain("\"Running\"", json);
            var (_, reopened) = await OpenEditorAsync(name, page);
            await reopened.GetByRole(AriaRole.Tab, new() { Name = "States" }).ClickAsync();
            await Assertions.Expect(reopened.GetByTestId("automation-state-node-Washing")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(reopened.GetByTestId("automation-state-edge")).ToHaveCountAsync(2);
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenAStateIsRenamedToAnotherStateOrToNothing_ThenAnErrorBlocksSavingUntilItIsCorrectedAndTheNameIsNotApplied()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "States" }).ClickAsync();
            await dialog.GetByTestId("automation-state-node-Running").ClickAsync();
            var stateName = dialog.GetByLabel("State name");
            var selected = dialog.GetByTestId("automation-selected-state");
            var save = dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });

            // Act
            await stateName.FillAsync("Idle");
            await stateName.PressAsync("Tab");

            // Assert
            await Assertions.Expect(selected).ToContainTextAsync("'Idle' is already a state", new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToContainTextAsync("'Idle' is already a state", new() { Timeout = Timeout });
            await Assertions.Expect(save).ToBeDisabledAsync();
            await Assertions.Expect(dialog.GetByTestId("automation-state-node-Running")).ToBeVisibleAsync();
            await stateName.FillAsync("  ");
            await stateName.PressAsync("Tab");
            await Assertions.Expect(selected).ToContainTextAsync("Enter a name", new() { Timeout = Timeout });
            await Assertions.Expect(save).ToBeDisabledAsync();
            await Assertions.Expect(dialog.GetByTestId("automation-state-node-Idle")).ToHaveCountAsync(1);
            await stateName.FillAsync("Washing");
            await stateName.PressAsync("Tab");
            await Assertions.Expect(dialog.GetByTestId("automation-state-node-Washing")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await Assertions.Expect(save).ToBeEnabledAsync();
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenAnInvalidRenameIsLeftByChangingTheTab_ThenTheErrorIsGoneAndTheStateKeepsItsName()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "States" }).ClickAsync();
            await dialog.GetByTestId("automation-state-node-Running").ClickAsync();
            await dialog.GetByLabel("State name").FillAsync("Idle");
            await dialog.GetByLabel("State name").PressAsync("Tab");
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToBeVisibleAsync(new() { Timeout = Timeout });

            // Act
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true })).ToBeEnabledAsync();
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "States" }).ClickAsync();
            await Assertions.Expect(dialog.GetByTestId("automation-state-node-Running")).ToBeVisibleAsync();
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheCurrentStateIsRenamedAndSaved_ThenAWarningShowsAndTheAutomationStopsWithAnErrorUntilTheStateIsReset()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await Assertions.Expect(page.GetByTestId("automation-status")).ToHaveTextAsync("Running", new() { Timeout = Timeout });
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "States" }).ClickAsync();
            await dialog.GetByTestId("automation-state-node-Idle").ClickAsync();
            await Assertions.Expect(dialog.GetByTestId("automation-current-state-warning")).ToContainTextAsync("This is the current state", new() { Timeout = Timeout });

            // Act
            var stateName = dialog.GetByLabel("State name");
            await stateName.FillAsync("Waiting");
            await stateName.PressAsync("Tab");

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-notices")).ToContainTextAsync("current state 'Idle'", new() { Timeout = Timeout });
            await SaveAsync(page, dialog);
            await Assertions.Expect(page.GetByTestId("automation-status")).ToHaveTextAsync("Error", new() { Timeout = Timeout });
            await page.GetByTestId("automation-reset").ClickAsync();
            await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Reset", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByTestId("automation-status")).ToHaveTextAsync("Running", new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenAnEdgeOfTheDiagramIsClicked_ThenItsRuleOpensInTheRulesTab()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "States" }).ClickAsync();

            // Act
            // The label, because the middle of the box of an edge is mostly empty space that takes no click.
            await dialog.GetByTestId("automation-state-edge").Filter(new() { HasTextString = "Finished" }).Locator("text").First.ClickAsync();

            // Assert
            await Assertions.Expect(dialog.GetByLabel("Rule name")).ToHaveValueAsync("Finished", new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenSpaceIsPressedOnAFocusedState_ThenTheStateIsSelectedAndThePageDoesNotScroll()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "States" }).ClickAsync();
            await page.EvaluateAsync("""
                () => {
                    window.__spacePrevented = [];
                    document.addEventListener('keydown', event => { if (event.key === ' ') { window.__spacePrevented.push(event.defaultPrevented); } });
                }
                """);

            // Act
            await dialog.GetByTestId("automation-state-node-Running").FocusAsync();
            await page.Keyboard.PressAsync("Space");

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-selected-state")).ToBeVisibleAsync(new() { Timeout = Timeout });
            var prevented = await page.EvaluateAsync<bool[]>("() => window.__spacePrevented");
            Assert.Contains(true, prevented);
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenANewStateIsDeletedAfterARejectedRename_ThenNoErrorStaysAndTheDialogCanBeSaved()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "States" }).ClickAsync();
            await dialog.GetByTestId("automation-add-state").ClickAsync();
            await Assertions.Expect(dialog.GetByLabel("State name")).ToHaveValueAsync("State 3", new() { Timeout = Timeout });
            await dialog.GetByLabel("State name").FillAsync("Idle");

            // Act
            // The click leaves the field first, which rejects the rename, and then deletes the state.
            await dialog.GetByTestId("automation-delete-state").ClickAsync();

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-selected-state")).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToHaveCountAsync(0);
            await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true })).ToBeEnabledAsync();
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenAStateIsRenamedToItsOwnNameWithASpace_ThenNothingIsRenamedAndTheDialogStaysUnchanged()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var (_, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "States" }).ClickAsync();
            await dialog.GetByTestId("automation-state-node-Running").ClickAsync();
            var stateName = dialog.GetByLabel("State name");

            // Act
            await stateName.FillAsync("Running ");
            await stateName.PressAsync("Tab");

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-state-node-Running")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByTestId("automation-renamed-state-warning")).ToHaveCountAsync(0);
            await Assertions.Expect(dialog.GetByTestId("automation-editor")).ToHaveAttributeAsync("data-dirty", "false");
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
