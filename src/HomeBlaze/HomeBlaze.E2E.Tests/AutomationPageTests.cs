using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

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
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            // Act
            var page = await OpenPageAsync(name);

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
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTestNowIsClicked_ThenTheDialogShowsTheTriggersAndWhyTheRuleWouldNotRun()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var page = await OpenPageAsync(name);
            await page.WaitUntilInteractiveAsync();

            // Act
            await page.GetByTestId("automation-rule-row").First.GetByTestId("automation-test-now").ClickAsync();

            // Assert
            var dialog = page.GetByRole(AriaRole.Dialog);
            await Assertions.Expect(dialog).ToContainTextAsync("Test now: Started", new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByTestId("automation-test-trigger")).ToContainTextAsync("/Demo/Motor/Temperature > 1000");
            await Assertions.Expect(dialog.GetByTestId("automation-test-verdict")).ToContainTextAsync("Would not run now");
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheAutomationIsShownAsAWidget_ThenItShowsTheCurrentStateAndTheStatus()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            await _fixture.WriteDemoPageAsync(ScratchPage,
                $"---\ntitle: Scratch\n---\n\n# Scratch\n\n:: HomeBlaze.Components.Widget Path=\"/{name}\"\n::\n");
            var page = await _fixture.CreatePageAsync();

            // Act
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");

            // Assert
            var widget = page.GetByTestId("automation-widget");
            await Assertions.Expect(widget).ToContainTextAsync("Washer", new() { Timeout = Timeout });
            await Assertions.Expect(widget).ToContainTextAsync("Idle since");
            await Assertions.Expect(widget.GetByTestId("automation-widget-status")).ToBeVisibleAsync();
            await Assertions.Expect(widget.GetByRole(AriaRole.Link, new() { Name = "Washer" })).ToHaveAttributeAsync("href", $"pages/{name}");
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheStateIsReset_ThenTheDiagramAndTheRuleStatusesFollow()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(WasherAutomation.Configure);
        try
        {
            var page = await OpenPageAsync(name);
            await page.WaitUntilInteractiveAsync();
            await Assertions.Expect(page.GetByTestId("automation-state-node-Idle")).ToContainTextAsync("Current", new() { Timeout = Timeout });
            automation.InitialState = "Running";
            await automation.ApplyConfigurationAsync(CancellationToken.None);

            // Act
            await page.GetByTestId("automation-reset").ClickAsync();
            await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Reset", Exact = true }).ClickAsync();

            // Assert
            await Assertions.Expect(page.GetByTestId("automation-state-node-Running")).ToContainTextAsync("Current", new() { Timeout = Timeout });
            await Assertions.Expect(page.GetByTestId("automation-state-node-Idle")).Not.ToContainTextAsync("Current");
            await Assertions.Expect(page.GetByTestId("automation-rule-row").Nth(1)).Not.ToContainTextAsync("Inactive outside state");
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    private async Task<IPage> OpenPageAsync(string name)
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/{name}");
        return page;
    }
}
