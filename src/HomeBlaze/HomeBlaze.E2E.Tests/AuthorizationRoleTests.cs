using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Abstractions;
using HomeBlaze.AI.Agents.ClaudeCli;
using HomeBlaze.Authorization;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for adding, renaming and deleting a custom role with the operations of the Authorization subject and its
/// roles, editing its rules in its dialog, picking a path into a rule, and giving the role to an agent.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class AuthorizationRoleTests
{
    private const int Timeout = 30000;
    private const string AgentPath = "Agents/TestAgent";
    private const string RoleName = "StateReader";
    private const string RenamedRoleName = "StateWatcher";
    private const string TypeName = "IConfigurable";
    private const string FolderPath = "/Agents";
    private const string FolderPattern = "/Agents/**";

    private readonly PlaywrightFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AuthorizationRoleTests(PlaywrightFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task WhenARoleIsAddedEditedGivenToAnAgentRenamedAndDeletedInTheUi_ThenTheFileFollowsAndTheAgentReportsInvalidRoles()
    {
        // Arrange
        var registry = _fixture.ServerServices.GetRequiredService<IInterceptorSubjectContext>().GetService<ISubjectRegistry>();
        var root = Assert.IsType<FluentStorageContainer>(_fixture.ServerServices.GetRequiredService<RootManager>().Root);
        var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
        var agent = Assert.IsType<ClaudeCliAgent>(resolver.ResolveSubject("/" + AgentPath, PathStyle.Canonical));
        var authorizationFilePath = _fixture.GetPagePath("Authorization.json");
        var agentFilePath = _fixture.GetPagePath(Path.Combine("Agents", "TestAgent.json"));
        var originalAgentFile = await _fixture.ReadFileAsync(agentFilePath);
        var originalRoles = agent.Roles;

        // Only one Authorization subject may exist, and the output copy outlives a run that was stopped before its cleanup.
        Assert.Empty(registry.KnownSubjects.Keys.OfType<Authorization.Authorization>());
        Assert.False(File.Exists(authorizationFilePath), "An Authorization.json of an earlier run is left in the test data.");

        var isCompleted = false;
        try
        {
            // Act: create the Authorization subject in the create dialog.
            var page = await SubjectBrowserPage.OpenAsync(_fixture, string.Empty);
            var paneCreateButton = page.Locator("#scrollContainer button:has-text('Create')").First;
            await Assertions.Expect(paneCreateButton).ToBeVisibleAsync(new() { Timeout = Timeout });
            await paneCreateButton.ClickAsync();
            await SubjectSetupDialogTests.PickTypeAsync(page, "authorization");
            await Assertions.Expect(page.GetByLabel("File name")).ToHaveValueAsync("Authorization");
            await Assertions.Expect(SubjectSetupDialogTests.CreateButton(page)).ToBeEnabledAsync(new() { Timeout = Timeout });
            await SubjectSetupDialogTests.CreateButton(page).ClickAsync();

            // Assert
            await Assertions.Expect(SubjectSetupDialogTests.Dialog(page)).Not.ToBeVisibleAsync(new() { Timeout = Timeout });
            await AsyncTestHelpers.WaitUntilAsync(
                () => registry.KnownSubjects.Keys.OfType<Authorization.Authorization>().Any() && File.Exists(authorizationFilePath),
                TimeSpan.FromSeconds(30),
                message: "The created Authorization subject is not in the graph or not stored.");

            // Act: add the role with the operation of the Authorization subject, which has no edit dialog.
            var authorizationPane = await OpenPaneAsync("Authorization");
            await Assertions.Expect(Method(authorizationPane, "Add Role")).ToBeEnabledAsync(new() { Timeout = Timeout });
            await Assertions.Expect(authorizationPane.GetByTestId("edit-subject-button")).ToHaveCountAsync(0);
            await RunOperationAsync(authorizationPane, "Add Role", RoleName, "Adds a custom role without rules");

            // Assert
            await WaitForAuthorizationFileAsync(
                authorizationFilePath,
                roles => roles[RoleName] is not null,
                "The added role was not written to Authorization.json.");

            // Act: add an allow rule in the dialog of the role, which is titled after it and scrolls only its tabs.
            var roleDialog = await OpenEditDialogAsync($"Authorization/{RoleName}");
            await Assertions.Expect(roleDialog).ToContainTextAsync($"Edit {RoleName}", new() { Timeout = Timeout });
            await Assertions.Expect(roleDialog.Locator(".role-edit-panels")).ToHaveCSSAsync("overflow-y", "auto");
            var allowTab = roleDialog.GetByTestId("role-edit-tab-allow");
            await Assertions.Expect(allowTab).ToHaveTextAsync("Allow (0)", new() { Timeout = Timeout });
            await allowTab.ClickAsync();
            await Assertions.Expect(roleDialog.GetByTestId("role-edit-allow-empty")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await roleDialog.GetByTestId("role-edit-add-allow").ClickAsync();

            // A new rule has no action yet, which the tab marks as invalid until one is checked.
            await Assertions.Expect(allowTab).ToHaveTextAsync("Allow (1)", new() { Timeout = Timeout });
            await Assertions.Expect(roleDialog.GetByTestId("role-edit-tab-allow-error")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(roleDialog.GetByTestId("role-edit-allow-empty")).ToHaveCountAsync(0);
            var rule = roleDialog.GetByTestId("role-edit-allow-rule-0");
            await rule.GetByTestId("rule-tags").Locator("input").FillAsync(MemberTags.State);
            await rule.Page.Locator(".mud-popover-open .mud-list-item")
                .Filter(new() { HasTextString = MemberTags.State })
                .First
                .ClickAsync();
            await Assertions.Expect(rule.GetByTestId("rule-tags-chip")).ToHaveTextAsync(MemberTags.State, new() { Timeout = Timeout });
            await rule.GetByTestId("rule-action-read").CheckAsync();
            await Assertions.Expect(roleDialog.GetByTestId("role-edit-tab-allow-error")).ToHaveCountAsync(0, new() { Timeout = Timeout });

            // A path picked in the dialog goes into the field, to be edited there and added with its button.
            var paths = rule.GetByTestId("rule-paths");
            await paths.GetByRole(AriaRole.Button, new() { Name = "Browse Paths" }).ClickAsync();
            var picker = rule.Page.GetByTestId("subject-path-picker");
            await picker.Locator("input").First.FillAsync(FolderPath);
            await rule.Page.GetByRole(AriaRole.Button, new() { Name = "OK", Exact = true }).ClickAsync();
            await Assertions.Expect(picker).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await Assertions.Expect(paths.Locator("input")).ToHaveValueAsync(FolderPattern, new() { Timeout = Timeout });
            await Assertions.Expect(paths.GetByTestId("rule-paths-chip")).ToHaveCountAsync(0);
            await paths.GetByTestId("rule-paths-add").ClickAsync();
            await Assertions.Expect(paths.GetByTestId("rule-paths-chip")).ToHaveTextAsync(FolderPattern, new() { Timeout = Timeout });
            await Assertions.Expect(paths.Locator("input")).ToHaveValueAsync(string.Empty);

            // Typed into a field with suggestions and saved at once, without Enter or leaving the field first.
            await rule.GetByTestId("rule-types").Locator("input").PressSequentiallyAsync(TypeName);
            await SaveAsync(roleDialog);

            // Assert
            await WaitForAuthorizationFileAsync(
                authorizationFilePath,
                roles => IsStateReadRule(roles[RoleName]?["allow"]?.AsArray().SingleOrDefault()),
                "The allow rule was not written to Authorization.json.");

            // Act: give the role to the agent in its dialog.
            var agentDialog = await OpenEditDialogAsync(AgentPath);
            var roles = agentDialog.GetByTestId("agent-edit-roles");
            await roles.Locator(".mud-select").First.ClickAsync();
            await agentDialog.Page.Locator(".mud-popover-open .mud-list-item")
                .Filter(new() { HasTextString = RoleName })
                .First
                .ClickAsync();
            await agentDialog.Page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(roles.GetByTestId($"agent-edit-role-{RoleName}")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await SaveAsync(agentDialog);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => agent.Roles.Contains(RoleName),
                TimeSpan.FromSeconds(30),
                message: "The role was not given to the agent.");

            var agentPage = await _fixture.CreatePageAsync();
            await agentPage.GotoAsync($"{_fixture.ServerAddress}pages/{AgentPath}");
            var settingsTab = agentPage.GetByRole(AriaRole.Tab, new() { Name = "Settings" });
            await Assertions.Expect(settingsTab).ToBeVisibleAsync(new() { Timeout = Timeout });
            await agentPage.WaitUntilInteractiveAsync(Timeout);
            await settingsTab.ClickAsync();
            var settingsRoles = agentPage.GetByTestId("agent-settings-roles");
            await Assertions.Expect(settingsRoles).ToContainTextAsync(RoleName, new() { Timeout = Timeout });
            await Assertions.Expect(agentPage.GetByTestId("agent-settings-roles-error")).ToHaveCountAsync(0);

            var authorizationFile = JsonNode.Parse(await _fixture.ReadFileAsync(authorizationFilePath))!;
            Assert.True(IsStateReadRule(authorizationFile["roles"]?[RoleName]?["allow"]?.AsArray().SingleOrDefault()));

            // Act: rename the role the agent uses with the operation of the role, whose dialog warns about the names in use.
            var rolePane = await OpenPaneAsync($"Authorization/{RoleName}");
            await RunOperationAsync(rolePane, "Rename", RenamedRoleName, "Agents and roles that already name the new name get its rules.");

            // Assert
            await WaitForAuthorizationFileAsync(
                authorizationFilePath,
                roles => roles[RoleName] is null && IsStateReadRule(roles[RenamedRoleName]?["allow"]?.AsArray().SingleOrDefault()),
                "The renamed role was not written to Authorization.json.");
            await AsyncTestHelpers.WaitUntilAsync(
                () => agent.StatusMessage?.StartsWith("Invalid roles: ", StringComparison.Ordinal) == true,
                TimeSpan.FromSeconds(30),
                message: "The agent does not report invalid roles.");

            agentPage = await _fixture.CreatePageAsync();
            await agentPage.GotoAsync($"{_fixture.ServerAddress}pages/{AgentPath}");
            settingsTab = agentPage.GetByRole(AriaRole.Tab, new() { Name = "Settings" });
            await Assertions.Expect(settingsTab).ToBeVisibleAsync(new() { Timeout = Timeout });
            await agentPage.WaitUntilInteractiveAsync(Timeout);
            await settingsTab.ClickAsync();
            await Assertions.Expect(agentPage.GetByTestId("agent-settings-roles-error"))
                .ToContainTextAsync($"Role '{RoleName}' is not defined.", new() { Timeout = Timeout });

            // Act: delete the renamed role with its operation, which takes the place of the Delete button of the pane.
            rolePane = await OpenPaneAsync($"Authorization/{RenamedRoleName}");
            var delete = Method(rolePane, "Delete");
            await Assertions.Expect(delete).ToBeEnabledAsync(new() { Timeout = Timeout });
            await Assertions.Expect(delete).ToHaveCountAsync(1);
            await Assertions.Expect(rolePane.Locator("button[aria-label='Delete']")).ToHaveCountAsync(0);
            await delete.ClickAsync();
            var confirmation = rolePane.Page.GetByRole(AriaRole.Dialog);
            await Assertions.Expect(confirmation).ToContainTextAsync("Confirm: Delete", new() { Timeout = Timeout });
            await Assertions.Expect(confirmation).ToContainTextAsync("report it as unknown afterwards");
            await confirmation.GetByRole(AriaRole.Button, new() { Name = "Execute", Exact = true }).ClickAsync();

            // Assert
            await WaitForAuthorizationFileAsync(
                authorizationFilePath,
                roles => roles.AsObject().Count == 0,
                "The deleted role was not removed from Authorization.json.");
            isCompleted = true;
        }
        finally
        {
            try
            {
                agent.Roles = originalRoles;
                await File.WriteAllTextAsync(agentFilePath, originalAgentFile);

                foreach (var authorization in registry.KnownSubjects.Keys.OfType<Authorization.Authorization>().ToList())
                {
                    await root.DeleteSubjectAsync(authorization, CancellationToken.None);
                }

                if (File.Exists(authorizationFilePath))
                {
                    File.Delete(authorizationFilePath);
                }
            }
            catch (Exception exception) when (!isCompleted)
            {
                // The failure of the test itself is the one to report.
                _output.WriteLine($"Restoring the test data failed: {exception}");
            }
        }
    }

    private static bool IsStateReadRule(JsonNode? rule) =>
        rule is not null &&
        JsonNode.DeepEquals(rule["paths"], new JsonArray(FolderPattern)) &&
        JsonNode.DeepEquals(rule["types"], new JsonArray(TypeName)) &&
        JsonNode.DeepEquals(rule["tags"], new JsonArray(MemberTags.State)) &&
        JsonNode.DeepEquals(rule["actions"], new JsonArray("read"));

    /// <summary>Opens the subject at the path in the subject browser and returns its pane, the last one.</summary>
    private async Task<ILocator> OpenPaneAsync(string path)
    {
        var page = await SubjectBrowserPage.OpenAsync(_fixture, path);

        // A path that does not resolve ends at an earlier pane, whose buttons belong to another subject.
        var paneCount = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length + 1;
        var panes = page.Locator("#scrollContainer [data-testid='subject-pane']");
        await Assertions.Expect(panes).ToHaveCountAsync(paneCount, new() { Timeout = Timeout });
        return panes.Last;
    }

    /// <summary>Opens the edit dialog of the subject at the path from its pane in the subject browser.</summary>
    private async Task<ILocator> OpenEditDialogAsync(string path)
    {
        var pane = await OpenPaneAsync(path);
        var editButton = pane.GetByTestId("edit-subject-button");
        await Assertions.Expect(editButton).ToBeVisibleAsync(new() { Timeout = Timeout });
        await editButton.ClickAsync();

        var dialog = pane.Page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = Timeout });
        return dialog;
    }

    private static ILocator Method(ILocator pane, string title) =>
        pane.Locator(".subject-pane-method").Filter(new() { HasTextString = title });

    /// <summary>
    /// Runs the operation of the pane that takes one text: checks that its dialog shows the description, enters the text,
    /// and waits until the dialog closed.
    /// </summary>
    private static async Task RunOperationAsync(ILocator pane, string title, string text, string description)
    {
        var operation = Method(pane, title);
        await Assertions.Expect(operation).ToBeEnabledAsync(new() { Timeout = Timeout });
        await operation.ClickAsync();

        var dialog = pane.Page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToContainTextAsync(title, new() { Timeout = Timeout });
        await Assertions.Expect(dialog).ToContainTextAsync(description);
        await dialog.Locator("input").First.FillAsync(text);
        var execute = dialog.GetByRole(AriaRole.Button, new() { Name = "Execute", Exact = true });
        await Assertions.Expect(execute).ToBeEnabledAsync(new() { Timeout = Timeout });
        await execute.ClickAsync();

        // A failing operation opens its error dialog after this one closes; the wait for the file that follows then fails.
        await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    private static async Task SaveAsync(ILocator dialog)
    {
        var saveButton = dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });
        await Assertions.Expect(saveButton).ToBeEnabledAsync(new() { Timeout = Timeout });
        await saveButton.ClickAsync();

        // The notification is the last step of the save, so everything it writes is written by now.
        await Assertions.Expect(dialog.Page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    // The server writes the file in place, so a read can see it held or partly written; the poll tries again.
    private static async Task WaitForAuthorizationFileAsync(string path, Func<JsonNode, bool> isExpected, string message)
    {
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                try
                {
                    return JsonNode.Parse(File.ReadAllText(path))?["roles"] is { } roles && isExpected(roles);
                }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(30),
            message: message);
    }
}
