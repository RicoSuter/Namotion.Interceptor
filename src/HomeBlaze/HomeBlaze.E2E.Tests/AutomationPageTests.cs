using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Automation;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.History;
using HomeBlaze.History.Abstractions;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the page of an automation: its status, its state diagram, its rules and Test now.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class AutomationPageTests
{
    private const int Timeout = 30000;
    private const string ScratchPage = "Scratch.md";

    private readonly PlaywrightFixture _fixture;

    public AutomationPageTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenTheAutomationPageIsOpened_ThenItShowsTheCurrentStateAndTheRules()
    {
        // Arrange
        await using var washer = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);

        // Act
        var page = await _fixture.OpenSubjectPageAsync(washer.Name);

        // Assert
        var automationPage = page.GetByTestId("automation-page");
        await Assertions.Expect(automationPage.GetByRole(AriaRole.Heading, new() { Name = "Washer" })).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("automation-state-node-Idle")).ToContainTextAsync("Current", new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("automation-state-node-Running")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("automation-state-edge")).ToHaveCountAsync(2);
        var rows = page.GetByTestId("automation-rule-row");
        await Assertions.Expect(rows).ToHaveCountAsync(2);
        await Assertions.Expect(rows.First).ToContainTextAsync("Started");
        await Assertions.Expect(rows.First).ToContainTextAsync("Dry run");
        await Assertions.Expect(rows.Nth(1)).ToContainTextAsync("Active in state Running");
        await Assertions.Expect(rows.First.GetByTestId("automation-dry-run-hint"))
            .ToHaveTextAsync("Dry runs do not change the state to Running.");
    }

    [Fact]
    public async Task WhenTestNowIsClicked_ThenTheDialogShowsTheTriggersAndWhyTheRuleWouldNotRun()
    {
        // Arrange
        await using var washer = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        var page = await _fixture.OpenSubjectPageAsync(washer.Name);

        // Act
        await page.GetByTestId("automation-rule-row").First.GetByTestId("automation-test-now").ClickAsync();

        // Assert
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToContainTextAsync("Test now: Started", new() { Timeout = Timeout });
        await Assertions.Expect(dialog.GetByTestId("automation-test-trigger")).ToContainTextAsync("/Demo/Motor/Temperature > 1000");
        await Assertions.Expect(dialog.GetByTestId("automation-test-verdict")).ToContainTextAsync("Would not run now");
    }

    [Fact]
    public async Task WhenTestNowIsClickedForARuleWhoseConditionIsTrue_ThenTheDialogSaysItWouldRun()
    {
        // Arrange
        await using var ready = await _fixture.AddStoredAutomationAsync(created =>
        {
            created.Name = "Ready";
            created.States = ["Idle", "Done"];
            created.InitialState = "Idle";
            created.Rules = new Dictionary<string, AutomationRule>
            {
                ["ready"] = new()
                {
                    Name = "Ready",
                    Mode = AutomationRuleMode.DryRun,
                    Triggers = [new PropertyTrigger { Expression = "../Name == Ready" }],
                    Actions = [new ChangeStateAction { Id = "state-1", TargetState = "Done" }]
                }
            };
        });
        var page = await _fixture.OpenSubjectPageAsync(ready.Name);
        await Assertions.Expect(page.GetByTestId("automation-status")).ToHaveTextAsync("Running", new() { Timeout = Timeout });

        // Act
        await page.GetByTestId("automation-rule-row").First.GetByTestId("automation-test-now").ClickAsync();

        // Assert
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog.GetByTestId("automation-test-verdict")).ToHaveTextAsync("Would run now.", new() { Timeout = Timeout });
        await Assertions.Expect(dialog.GetByTestId("automation-test-trigger")).ToContainTextAsync("true");
        await Assertions.Expect(dialog).ToContainTextAsync("ChangeState(Done)");
    }

    [Fact]
    public async Task WhenTheAutomationIsShownAsAWidget_ThenItShowsTheCurrentStateAndTheStatus()
    {
        // Arrange
        await using var washer = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        var scratchContent = await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(ScratchPage));
        try
        {
            await _fixture.WriteDemoPageAsync(ScratchPage,
                $"---\ntitle: Scratch\n---\n\n# Scratch\n\n:: HomeBlaze.Components.Widget Path=\"/{washer.Name}\"\n::\n");
            var page = await _fixture.CreatePageAsync();

            // Act
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");

            // Assert
            var widget = page.GetByTestId("automation-widget");
            await Assertions.Expect(widget).ToContainTextAsync("Washer", new() { Timeout = Timeout });
            await Assertions.Expect(widget).ToContainTextAsync("Idle since");
            await Assertions.Expect(widget.GetByTestId("automation-widget-status")).ToBeVisibleAsync();
            await Assertions.Expect(widget.GetByRole(AriaRole.Link, new() { Name = "Washer" })).ToHaveAttributeAsync("href", $"/pages/{washer.Name}");
        }
        finally
        {
            await _fixture.WriteDemoPageAsync(ScratchPage, scratchContent);
        }
    }

    [Fact]
    public async Task WhenTheStateIsReset_ThenTheDiagramAndTheRuleStatusesFollow()
    {
        // Arrange
        await using var washer = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        var automation = washer.Subject;
        var page = await _fixture.OpenSubjectPageAsync(washer.Name);
        await Assertions.Expect(page.GetByTestId("automation-state-node-Idle")).ToContainTextAsync("Current", new() { Timeout = Timeout });
        automation.InitialState = "Running";
        await automation.ApplyConfigurationAsync(CancellationToken.None);
        await page.GetByTestId("automation-reset").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
        Assert.Equal("Idle", automation.CurrentState);

        // Act
        await ResetStateAsync(page);

        // Assert
        await Assertions.Expect(page.GetByTestId("automation-state-node-Running")).ToContainTextAsync("Current", new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("automation-state-node-Idle")).Not.ToContainTextAsync("Current");
        await Assertions.Expect(page.GetByTestId("automation-rule-row").Nth(1)).Not.ToContainTextAsync("Inactive outside state");
    }

    [Fact]
    public async Task WhenARuleRuns_ThenThePageListsTheRunFromHistoryWithItsOutcomeAndMessage()
    {
        // Arrange
        await using var recorder = await AddRecorderAsync();
        var page = await _fixture.OpenSubjectPageAsync(recorder.Name);
        await OpenRunsTabAsync(page);
        await Assertions.Expect(page.GetByTestId("automation-no-runs")).ToBeVisibleAsync(new() { Timeout = Timeout });
        // A run is a count that follows a stored one, so the restored count must be stored before the run.
        await WaitUntilRunCountIsStoredAsync(recorder.Subject.Rules["ran"], 0);

        // Act
        recorder.Subject.Name = "Go";

        // Assert
        var run = page.GetByTestId("automation-run");
        await Assertions.Expect(run).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Assertions.Expect(run).ToContainTextAsync("Ran");
        await Assertions.Expect(run).ToContainTextAsync("Would have run (dry run)");
        // A dry run describes its actions as text, which the Actions cell shows as one line.
        await Assertions.Expect(run.GetByTestId("automation-run-action")).ToHaveTextAsync("Would run ChangeState(Done)");
        await Assertions.Expect(page.GetByTestId("automation-tab-runs-count")).ToHaveTextAsync("1");
        await Assertions.Expect(run).ToContainTextAsync(new Regex(@"\d:\d\d:\d\d"));
        await Assertions.Expect(page.GetByText("From history: the runs of the last seven days. Runs within about 250 ms can show as one, and a crash can drop runs.")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WhenARuleWritesAProperty_ThenTheRunListsTheActionWithItsSubjectAsALink()
    {
        // Arrange
        await using var motor = await _fixture.AddStoredMotorAsync();
        await using var writer = await AddWriterAsync(motor.Name);
        var page = await _fixture.OpenSubjectPageAsync(writer.Name);
        await OpenRunsTabAsync(page);
        await Assertions.Expect(page.GetByTestId("automation-no-runs")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await WaitUntilRunCountIsStoredAsync(writer.Subject.Rules["write"], 0);

        // Act
        writer.Subject.Name = "Go";

        // Assert
        var action = page.GetByTestId("automation-run").GetByTestId("automation-run-action");
        await Assertions.Expect(action).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Assertions.Expect(action).ToContainTextAsync("IsPowered");
        await Assertions.Expect(action).ToContainTextAsync("= False");
        var link = action.GetByRole(AriaRole.Link);
        await Assertions.Expect(link).ToHaveAttributeAsync("title", $"/{motor.Name}");
        await Assertions.Expect(link).ToHaveAttributeAsync("href", new Regex(Regex.Escape(motor.Name)));
    }

    [Fact]
    public async Task WhenARuleWithoutRunsIsSelected_ThenTheRunsTabSaysSoAndKeepsTheSelectionAcrossTabs()
    {
        // Arrange
        await using var motor = await _fixture.AddStoredMotorAsync();
        await using var writer = await AddWriterAsync(motor.Name);
        var page = await _fixture.OpenSubjectPageAsync(writer.Name);
        await OpenRunsTabAsync(page);
        await Assertions.Expect(page.GetByTestId("automation-no-runs")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await WaitUntilRunCountIsStoredAsync(writer.Subject.Rules["write"], 0);
        writer.Subject.Name = "Go";
        await Assertions.Expect(page.GetByTestId("automation-run")).ToHaveCountAsync(1, new() { Timeout = Timeout });

        // Act
        await page.GetByTestId("automation-run-filter").GetByText("Idle", new() { Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("automation-no-rule-runs")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await page.GetByRole(AriaRole.Tab, new() { Name = "Overview" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("automation-rule-row").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await OpenRunsTabAsync(page);

        // Assert
        await Assertions.Expect(page.GetByTestId("automation-no-rule-runs")).ToHaveTextAsync("No runs of this rule.", new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("automation-run")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenRunsAreShownOnAPhone_ThenEachRunIsACard()
    {
        // Arrange
        await using var motor = await _fixture.AddStoredMotorAsync();
        await using var writer = await AddWriterAsync(motor.Name);
        var page = await _fixture.CreatePageAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
        await page.GotoAsync($"{_fixture.ServerAddress}pages/{writer.Name}");
        await page.WaitUntilInteractiveAsync();
        await OpenRunsTabAsync(page);
        await Assertions.Expect(page.GetByTestId("automation-no-runs")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await WaitUntilRunCountIsStoredAsync(writer.Subject.Rules["write"], 0);

        // Act
        writer.Subject.Name = "Go";

        // Assert
        var card = page.GetByTestId("automation-run-card");
        await Assertions.Expect(card).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Assertions.Expect(card).ToBeVisibleAsync();
        await Assertions.Expect(card).ToContainTextAsync("Write");
        await Assertions.Expect(card.GetByTestId("automation-run-action")).ToContainTextAsync("IsPowered");
        await Assertions.Expect(page.GetByTestId("automation-run")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task WhenThePageNavigatesToAnotherAutomation_ThenItShowsNoRunsOfThePreviousOne()
    {
        // Arrange
        await using var recorder = await AddRecorderAsync();
        await using var washer = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        var page = await _fixture.OpenSubjectPageAsync(recorder.Name);
        await OpenRunsTabAsync(page);
        await Assertions.Expect(page.GetByTestId("automation-no-runs")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await WaitUntilRunCountIsStoredAsync(recorder.Subject.Rules["ran"], 0);
        recorder.Subject.Name = "Go";
        await Assertions.Expect(page.GetByTestId("automation-run")).ToHaveCountAsync(1, new() { Timeout = Timeout });

        // Act
        // Navigating within the circuit passes the other automation to the same page component.
        await page.EvaluateAsync("url => Blazor.navigateTo(url)", $"/pages/{washer.Name}");

        // Assert
        var automationPage = page.GetByTestId("automation-page");
        await Assertions.Expect(automationPage.GetByRole(AriaRole.Heading, new() { Name = "Washer" })).ToBeVisibleAsync(new() { Timeout = Timeout });
        // Read once, without waiting, because the next read of the runs would replace a stale list after a few seconds.
        Assert.Equal(0, await page.GetByTestId("automation-run").CountAsync());
        await Assertions.Expect(page.GetByTestId("automation-no-runs")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenARuleRowIsClicked_ThenTheEditorOpensOnThatRule()
    {
        // Arrange
        await using var washer = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        var page = await _fixture.OpenSubjectPageAsync(washer.Name);
        var row = page.GetByTestId("automation-rule-row").Nth(1);
        await Assertions.Expect(row).ToContainTextAsync("Finished", new() { Timeout = Timeout });

        // Act
        await row.GetByRole(AriaRole.Button, new() { Name = "Edit rule Finished" }).ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("automation-editor")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".automation-rule-select[aria-current=true]")).ToContainTextAsync("Finished");
    }

    [Fact]
    public async Task WhenTestNowIsClickedInARuleRow_ThenOnlyTheTestDialogOpens()
    {
        // Arrange
        await using var washer = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        var page = await _fixture.OpenSubjectPageAsync(washer.Name);

        // Act
        await page.GetByTestId("automation-rule-row").First.GetByTestId("automation-test-now").ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("Test now: Started", new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("automation-editor")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenNotesAreSaved_ThenTheOverviewShowsThem()
    {
        // Arrange
        await using var washer = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        var page = await _fixture.OpenSubjectPageAsync(washer.Name);
        var automationPage = page.GetByTestId("automation-page");
        await Assertions.Expect(automationPage.GetByTestId("automation-no-notes")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await automationPage.GetByTestId("automation-notes-edit").ClickAsync();
        // MudDialog puts its test id on its content, which does not hold the actions.
        var dialog = page.Locator(".mud-dialog").Filter(new() { Has = page.GetByTestId("metadata-edit-dialog") });
        await dialog.GetByTestId("metadata-notes-input").FillAsync("Why this **exists**");

        // Act
        await dialog.GetByTestId("metadata-save").ClickAsync();

        // Assert
        await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(automationPage.GetByTestId("metadata-notes").Locator("strong")).ToHaveTextAsync("exists", new() { Timeout = Timeout });
        await Assertions.Expect(automationPage.GetByTestId("automation-no-notes")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenNotesAreSavedInAnotherBrowser_ThenTheOverviewShowsThemAndTheirChanges()
    {
        // Arrange
        await using var washer = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        var page = await _fixture.OpenSubjectPageAsync(washer.Name);
        await using var otherContext = await _fixture.Browser.NewContextAsync();
        var otherPage = await otherContext.NewPageAsync();
        await otherPage.GotoAsync($"{_fixture.ServerAddress}pages/{washer.Name}");
        await otherPage.WaitUntilInteractiveAsync();
        var notes = page.GetByTestId("automation-page").GetByTestId("metadata-notes");
        await Assertions.Expect(page.GetByTestId("automation-no-notes")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await SaveNotesAsync(otherPage, "First notes");
        await Assertions.Expect(notes).ToHaveTextAsync("First notes", new() { Timeout = Timeout });
        await SaveNotesAsync(otherPage, "Second notes");

        // Assert
        await Assertions.Expect(notes).ToHaveTextAsync("Second notes", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenThePageIsShownOnAPhone_ThenTheRulesAreCards()
    {
        // Arrange
        await using var washer = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        var page = await _fixture.CreatePageAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}pages/{washer.Name}");
        await page.WaitUntilInteractiveAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("automation-rule-card")).ToHaveCountAsync(2, new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("automation-rule-card").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("automation-rule-row").First).ToBeHiddenAsync();
        await Assertions.Expect(page.GetByTestId("automation-reset-icon")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("automation-reset")).ToBeHiddenAsync();
    }

    /// <summary>Adds an automation whose rule runs as a dry run once the automation is named Go.</summary>
    private Task<StoredSubject<HomeBlaze.Automation.Automation>> AddRecorderAsync() =>
        _fixture.AddStoredAutomationAsync(created =>
        {
            created.Name = "Recorder";
            created.States = ["Idle", "Done"];
            created.InitialState = "Idle";
            created.Rules = new Dictionary<string, AutomationRule>
            {
                ["ran"] = new()
                {
                    Name = "Ran",
                    Mode = AutomationRuleMode.DryRun,
                    Triggers = [new PropertyTrigger { Expression = "../Name == Go" }],
                    Actions = [new ChangeStateAction { Id = "state-1", TargetState = "Done" }]
                }
            };
        });

    /// <summary>Adds an automation whose rule Write sets IsPowered of the motor to false once the automation is named Go, and whose rule Idle never runs.</summary>
    private Task<StoredSubject<HomeBlaze.Automation.Automation>> AddWriterAsync(string motorName) =>
        _fixture.AddStoredAutomationAsync(created =>
        {
            created.Name = "Writer";
            created.RuleOrder = ["write", "idle"];
            created.Rules = new Dictionary<string, AutomationRule>
            {
                ["write"] = new()
                {
                    Name = "Write",
                    Mode = AutomationRuleMode.On,
                    Triggers = [new PropertyTrigger { Expression = "../Name == Go" }],
                    Actions = [new SetPropertyAction { Id = "set-1", PropertyPath = $"/{motorName}/IsPowered", Value = "false" }]
                },
                ["idle"] = new()
                {
                    Name = "Idle",
                    Mode = AutomationRuleMode.DryRun,
                    Triggers = [new PropertyTrigger { Expression = "../Name == Never" }],
                    Actions = [new SetPropertyAction { Id = "set-1", PropertyPath = $"/{motorName}/IsPowered", Value = "true" }]
                }
            };
        });

    /// <summary>Writes the notes of the automation of the page with the pencil and the metadata dialog.</summary>
    private static async Task SaveNotesAsync(IPage page, string notes)
    {
        await page.GetByTestId("automation-notes-edit").ClickAsync();
        // MudDialog puts its test id on its content, which does not hold the actions.
        var dialog = page.Locator(".mud-dialog").Filter(new() { Has = page.GetByTestId("metadata-edit-dialog") });
        await dialog.GetByTestId("metadata-notes-input").FillAsync(notes);
        await dialog.GetByTestId("metadata-save").ClickAsync();
        await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    private static Task OpenRunsTabAsync(IPage page) =>
        page.GetByRole(AriaRole.Tab, new() { Name = "Runs" }).ClickAsync();

    /// <summary>Resets the state with the Reset state button of the page and confirms it.</summary>
    internal static async Task ResetStateAsync(IPage page)
    {
        await page.GetByTestId("automation-reset").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Reset", Exact = true }).ClickAsync();
    }

    private async Task WaitUntilRunCountIsStoredAsync(AutomationRule rule, long runCount)
    {
        var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
        var path = resolver.GetPath(rule, PathStyle.Canonical) + "/" + nameof(AutomationRule.RunCount);
        var registry = _fixture.ServerServices.GetRequiredService<IInterceptorSubjectContext>().GetService<ISubjectRegistry>();

        // The condition is synchronous, so a poll starts a lookup and a later poll reads its result.
        Task<HistoryPoint?>? lookup = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                if (lookup is { IsCompletedSuccessfully: true, Result.Number: { } number } && (long)number == runCount)
                {
                    return true;
                }

                if (lookup is null || lookup.IsCompleted)
                {
                    lookup = registry.GetValueAtAsync(path, DateTimeOffset.UtcNow, CancellationToken.None);
                }

                return false;
            },
            TimeSpan.FromSeconds(30),
            message: $"The history did not store the run count {runCount} of {path}");
    }
}
