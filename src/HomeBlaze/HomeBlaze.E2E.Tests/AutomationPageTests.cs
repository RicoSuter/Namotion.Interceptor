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
            .ToHaveTextAsync("Dry runs do not change the state, so the rules of Running do not run until this rule is On.");
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
        // The rule runs as a dry run once the automation is named Go.
        await using var recorder = await _fixture.AddStoredAutomationAsync(created =>
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
        var page = await _fixture.OpenSubjectPageAsync(recorder.Name);
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
        await Assertions.Expect(run).ToContainTextAsync("Would run ChangeState(Done)");
        await Assertions.Expect(page.GetByText("From history: the runs of the last seven days. Runs within about 250 ms can show as one, and a crash can drop runs.")).ToBeVisibleAsync();
    }

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
