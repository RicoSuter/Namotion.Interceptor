using System.Text.Json;
using HomeBlaze.Automation;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the roles of an automation: the editor lists an action the roles do not allow until a role allows it, a
/// stored automation with such an action is not applied, and roles that cannot be used are named on the page.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class AutomationAuthorizationTests
{
    private const int Timeout = 30000;
    private const string Denial = "/Demo/Motor/RunTest: no role of the automation allows calling it";

    private readonly PlaywrightFixture _fixture;

    public AutomationAuthorizationTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAnActionCallsAnOperationTheRolesDoNotAllow_ThenTheEditorListsItUntilARoleAllowsIt()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Roles = ["Reader"];
        });
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await AddRunTestCallAsync(dialog);
            var save = dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToContainTextAsync(Denial, new() { Timeout = Timeout });
            await Assertions.Expect(save).ToBeDisabledAsync();

            // Act
            await AddRoleAsync(page, dialog, "Operator");

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await Assertions.Expect(save).ToBeEnabledAsync();
            await save.ClickAsync();
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(dialog).ToHaveCountAsync(0);
            using var saved = JsonDocument.Parse(await _fixture.ReadFileAsync(filePath));
            var roles = saved.RootElement.GetProperty("roles").EnumerateArray().Select(role => role.GetString()!).ToArray();
            Assert.Equal(["Reader", "Operator"], roles);
            await Assertions.Expect(page.GetByTestId("automation-roles")).ToContainTextAsync("Reader, Operator", new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheRolesAreChangedInTheGeneralTab_ThenTheDialogIsDirtyAndTheActionsAreCheckedAgain()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Roles = ["Reader"];
            AddRunTest(configured);
        });
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            var editor = dialog.GetByTestId("automation-editor");
            await Assertions.Expect(editor).ToHaveAttributeAsync("data-dirty", "false");
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToContainTextAsync(Denial, new() { Timeout = Timeout });

            // Act
            await AddRoleAsync(page, dialog, "Operator");

            // Assert
            await Assertions.Expect(editor).ToHaveAttributeAsync("data-dirty", "true", new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToHaveCountAsync(0, new() { Timeout = Timeout });

            // Act
            await dialog.GetByTestId("automation-role-Operator").GetByRole(AriaRole.Button).ClickAsync();

            // Assert
            await Assertions.Expect(dialog.GetByTestId("automation-role-Operator")).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByTestId("automation-errors")).ToContainTextAsync(Denial, new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenTheCallOperationIsBrowsed_ThenOnlyOperationsTheRolesAllowAreOffered()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Roles = ["Reader"];
        });
        try
        {
            var (page, dialog) = await OpenEditorAsync(name);
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            await dialog.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Started" }).ClickAsync();
            await dialog.GetByTestId("automation-add-call-operation").ClickAsync();
            var action = dialog.GetByTestId("automation-action").Last;

            // Act
            var picker = await BrowseMotorAsync(page, action);

            // Assert
            await Assertions.Expect(picker.GetByText("Temperature", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(picker.GetByText("RunTest", new() { Exact = true })).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).Last.ClickAsync();
            await Assertions.Expect(picker).ToHaveCountAsync(0, new() { Timeout = Timeout });

            // Act
            await AddRoleAsync(page, dialog, "Operator");
            await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
            picker = await BrowseMotorAsync(page, dialog.GetByTestId("automation-action").Last);

            // Assert
            await Assertions.Expect(picker.GetByText("RunTest", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenAStoredActionIsNotAllowedByTheRoles_ThenTheConfigurationIsNotAppliedAndThePageSaysWhy()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Roles = ["Reader"];
            AddRunTest(configured);
        });
        try
        {
            // Act
            var page = await OpenPageAsync(name);

            // Assert
            await Assertions.Expect(page.GetByTestId("automation-status")).ToHaveTextAsync("Error", new() { Timeout = Timeout });
            await Assertions.Expect(page.GetByTestId("automation-page")).ToContainTextAsync(Denial, new() { Timeout = Timeout });
            await Assertions.Expect(page.GetByTestId("automation-roles")).ToContainTextAsync("Reader");
            await Assertions.Expect(page.GetByTestId("automation-roles-error")).ToHaveCountAsync(0);
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    [Fact]
    public async Task WhenAnAutomationThatCallsAnOperationHasNoRoles_ThenItRunsAndThePageSaysItsCallsAreRefused()
    {
        // Arrange
        var (name, filePath, automation) = await _fixture.AddStoredAutomationAsync(configured =>
        {
            WasherAutomation.Configure(configured);
            configured.Roles = [];
            AddRunTest(configured);
        });
        try
        {
            // Act
            var page = await OpenPageAsync(name);

            // Assert
            await Assertions.Expect(page.GetByTestId("automation-status")).ToHaveTextAsync("Running", new() { Timeout = Timeout });
            await Assertions.Expect(page.GetByTestId("automation-roles")).ToContainTextAsync("none");
            await Assertions.Expect(page.GetByTestId("automation-roles-error")).ToHaveTextAsync(
                "The actions that call operations or write properties are refused: the roles of the automation cannot be used: No roles are assigned.",
                new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.DeleteStoredSubjectAsync(filePath, automation);
        }
    }

    // A call of the demo motor in the dry-run rule Started, whose condition never becomes true, so nothing runs.
    private static void AddRunTest(HomeBlaze.Automation.Automation automation)
    {
        var started = automation.Rules["started"];
        started.Actions =
        [
            .. started.Actions,
            new InvokeOperationAction
            {
                Id = "call-1",
                OperationPath = "/Demo/Motor/RunTest",
                Arguments = new Dictionary<string, string> { ["speed"] = "50", ["durationSeconds"] = "5" }
            }
        ];
    }

    private static async Task AddRunTestCallAsync(ILocator dialog)
    {
        await dialog.GetByRole(AriaRole.Tab, new() { Name = "Rules" }).ClickAsync();
        await dialog.GetByTestId("automation-rule-item").Filter(new() { HasTextString = "Started" }).ClickAsync();
        await dialog.GetByTestId("automation-add-call-operation").ClickAsync();
        var action = dialog.GetByTestId("automation-action").Last;
        await action.GetByLabel("Call operation", new() { Exact = true }).FillAsync("/Demo/Motor/RunTest");
        await action.GetByLabel("Speed").FillAsync("50");
        await action.GetByLabel("DurationSeconds").FillAsync("5");
    }

    private static async Task<ILocator> BrowseMotorAsync(IPage page, ILocator action)
    {
        await action.GetByLabel("Call operation", new() { Exact = true }).FillAsync("/Demo/Motor");
        await action.GetByLabel("Browse Call operation").ClickAsync();
        var picker = page.GetByTestId("subject-path-picker-dialog");
        await Assertions.Expect(picker).ToBeVisibleAsync(new() { Timeout = Timeout });
        foreach (var segment in new[] { "Root", "Demo" })
        {
            await picker.GetByText(segment, new() { Exact = true }).ClickAsync();
        }

        await picker.Locator(".mud-treeview-item-content").Filter(new() { Has = page.GetByText("Motor", new() { Exact = true }) }).GetByLabel("Expand").ClickAsync();
        return picker;
    }

    private static async Task AddRoleAsync(IPage page, ILocator dialog, string role)
    {
        await dialog.GetByRole(AriaRole.Tab, new() { Name = "General" }).ClickAsync();
        var roles = dialog.GetByTestId("automation-roles");
        await roles.Locator(".mud-select").First.ClickAsync();
        await page.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasTextString = role }).First.ClickAsync();
        await roles.Locator(".mud-select input").First.PressAsync("Escape");
        await Assertions.Expect(roles.GetByTestId($"automation-role-{role}")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".mud-popover-open")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    private async Task<(IPage Page, ILocator Dialog)> OpenEditorAsync(string name)
    {
        var page = await OpenPageAsync(name);
        await page.GetByTestId("automation-edit").ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog.GetByTestId("automation-editor")).ToBeVisibleAsync(new() { Timeout = Timeout });
        return (page, dialog);
    }

    private async Task<IPage> OpenPageAsync(string name)
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/{name}");
        await page.WaitUntilInteractiveAsync();
        return page;
    }
}
