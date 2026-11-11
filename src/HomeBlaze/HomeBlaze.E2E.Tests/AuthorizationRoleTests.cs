using System.Text.Json.Nodes;
using HomeBlaze.Abstractions;
using HomeBlaze.AI.Agents.ClaudeCli;
using HomeBlaze.Components.Abstractions.Access;
using HomeBlaze.Authorization;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for adding, renaming and deleting custom roles with the operations of the Authorization subject and its roles,
/// editing a role in its dialog, picking a path into a rule, previewing a role and giving it to an agent.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class AuthorizationRoleTests
{
    private const string AgentPath = "Agents/TestAgent";
    private const string RoleName = "state-reader";
    private const string RenamedRoleName = "state-watcher";
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
    public async Task WhenARoleIsAddedEditedGivenToAnAgentRenamedAndDeletedInTheUi_ThenTheRoleStoreHoldsItAndTheAgentFollowsTheRename()
    {
        // Arrange
        var registry = _fixture.ServerServices.GetRequiredService<IInterceptorSubjectContext>().GetService<ISubjectRegistry>();
        var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
        var roleStore = _fixture.ServerServices.GetRequiredService<IRoleStore>();
        var agent = Assert.IsType<ClaudeCliAgent>(resolver.ResolveSubject("/" + AgentPath, PathStyle.Canonical));
        var authorizationFilePath = _fixture.GetPagePath("Authorization.json");
        var agentFilePath = _fixture.GetPagePath(Path.Combine("Agents", "TestAgent.json"));
        var originalAgentFile = await _fixture.ReadFileAsync(agentFilePath);
        var originalRoles = agent.Roles;

        // The startup adds the single Authorization subject to the root storage, and the roles come from the database.
        var authorization = Assert.Single(registry.KnownSubjects.Keys.OfType<Authorization.Authorization>());
        Assert.True(File.Exists(authorizationFilePath));
        Assert.Null(roleStore.GetConcurrencyStamp(RoleName));
        Assert.Null(roleStore.GetConcurrencyStamp(RenamedRoleName));

        var isCompleted = false;
        try
        {
            // Act: add the role with the operation of the Authorization subject, which has no edit dialog.
            var authorizationPane = await OpenPaneAsync("Authorization");
            await Assertions.Expect(Method(authorizationPane, "Add Role")).ToBeEnabledAsync();
            await Assertions.Expect(authorizationPane.GetByTestId("edit-subject-button")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
            await RunOperationAsync(authorizationPane, "Add Role", RoleName, "Adds a custom role without rules");

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => authorization.Roles.ContainsKey(RoleName) && roleStore.GetConcurrencyStamp(RoleName) is not null,
                message: "The added role was not stored.");

            // Act: add an allow rule in the dialog of the role, which is titled after it and keeps its height.
            var roleDialog = await OpenEditDialogAsync($"Authorization/{RoleName}");
            await Assertions.Expect(roleDialog).ToContainTextAsync($"Edit {RoleName}");
            var allowTab = roleDialog.GetByTestId("role-edit-tab-allow");
            await Assertions.Expect(allowTab).ToHaveTextAsync("Allow (0)");
            await allowTab.ClickAsync();
            await Assertions.Expect(roleDialog.GetByTestId("role-edit-allow-empty")).ToBeVisibleAsync();
            var editor = roleDialog.GetByTestId("role-edit");
            // The layout height, which the dialog's opening animation, a scale transform, leaves as it is.
            var editorHeight = await GetLayoutHeightAsync(editor);
            await roleDialog.GetByTestId("role-edit-add-allow").ClickAsync();

            // A new rule has no action yet, which the tab marks as invalid until one is checked.
            await Assertions.Expect(allowTab).ToHaveTextAsync("Allow (1)");
            await Assertions.Expect(roleDialog.GetByTestId("role-edit-tab-allow-error")).ToBeVisibleAsync();
            await Assertions.Expect(roleDialog.GetByTestId("role-edit-allow-empty")).ToHaveCountAsync(0);
            var rule = roleDialog.GetByTestId("role-edit-allow-rule-0");
            await rule.GetByTestId("rule-tags").Locator("input").FillAsync(MemberTags.State);
            await rule.Page.Locator(".mud-popover-open .mud-list-item")
                .Filter(new() { HasTextString = MemberTags.State })
                .First
                .ClickAsync();
            await Assertions.Expect(rule.GetByTestId("rule-tags-chip")).ToHaveTextAsync(MemberTags.State);
            await rule.GetByTestId("rule-action-read").CheckAsync();
            await Assertions.Expect(roleDialog.GetByTestId("role-edit-tab-allow-error")).ToHaveCountAsync(0);

            // A rule card leaves the height of the editor as it was; what does not fit scrolls inside the tab.
            Assert.Equal(editorHeight, await GetLayoutHeightAsync(editor));
            Assert.True(await editor.EvaluateAsync<bool>("element => element.scrollHeight <= element.clientHeight"));

            // A path picked in the dialog goes into the field, to be edited there and added with its button.
            var paths = rule.GetByTestId("rule-paths");
            await paths.GetByRole(AriaRole.Button, new() { Name = "Browse Paths" }).ClickAsync();
            var picker = rule.Page.GetByTestId("subject-path-picker");
            await picker.Locator("input").First.FillAsync(FolderPath);
            await rule.Page.GetByRole(AriaRole.Button, new() { Name = "OK", Exact = true }).ClickAsync();
            await Assertions.Expect(picker).ToHaveCountAsync(0);
            await Assertions.Expect(paths.Locator("input")).ToHaveValueAsync(FolderPattern);
            await Assertions.Expect(paths.GetByTestId("rule-paths-chip")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
            await paths.GetByTestId("rule-paths-add").ClickAsync();
            await Assertions.Expect(paths.GetByTestId("rule-paths-chip")).ToHaveTextAsync(FolderPattern);
            await Assertions.Expect(paths.Locator("input")).ToHaveValueAsync(string.Empty);

            // Entered into a field with suggestions and saved at once, without Enter or leaving the field first. Filled
            // rather than typed key by key, which can lose keys when the focus moves or the field renders while typing.
            // The value check catches only a render that replaced the text in the browser before the save, not text the
            // server never received; the failure below shows the roles in memory for that.
            var typesInput = rule.GetByTestId("rule-types").Locator("input");
            await typesInput.FillAsync(TypeName);
            await Assertions.Expect(typesInput).ToHaveValueAsync(TypeName);
            await SaveAsync(roleDialog);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => authorization.Roles.TryGetValue(RoleName, out var role) && IsStateReadRule(role.Allow.SingleOrDefault()),
                () => $"The allow rule was not stored. The roles in memory: {DescribeRoles(authorization)}");

            // A full administrator is never refused, also not when the dialog persists the role it saved.
            await Assertions.Expect(roleDialog.Page.GetByText(AccessDeniedTexts.Administer)).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });

            // Roles live in the database only, never in the file of the Authorization subject.
            Assert.Null(JsonNode.Parse(await _fixture.ReadFileAsync(authorizationFilePath))!["roles"]);

            // Act: preview the role in a short window, where the whole tab scrolls and the header row of the matrix sticks
            // to its top.
            var previewDialog = await OpenEditDialogAsync($"Authorization/{RoleName}");
            await previewDialog.Page.SetViewportSizeAsync(1280, 700);
            await previewDialog.GetByTestId("role-edit-tab-preview").ClickAsync();
            await previewDialog.GetByTestId("role-preview-path").Locator("input").FillAsync("/" + AgentPath);
            await Assertions.Expect(previewDialog.GetByTestId("role-preview-matrix")).ToBeVisibleAsync();
            var header = await previewDialog.EvaluateAsync<double[]>(
                """
                dialog => {
                    const panels = dialog.querySelector('.role-edit-panels');
                    const header = dialog.querySelector('.role-preview-matrix thead th');
                    const offset = header.getBoundingClientRect().top - panels.getBoundingClientRect().top;
                    panels.scrollTop = panels.scrollHeight;
                    return [offset, panels.scrollTop, header.getBoundingClientRect().top - panels.getBoundingClientRect().top];
                }
                """);

            // Assert
            Assert.True(header[1] > header[0], "The tab did not scroll past the header row of the matrix.");
            Assert.InRange(header[2], -1, 1);
            await previewDialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();

            // Act: give the role to the agent in its dialog.
            var agentDialog = await OpenEditDialogAsync(AgentPath);
            var roles = agentDialog.GetByTestId("agent-edit-roles");
            await roles.Locator(".mud-select").First.ClickAsync();
            await agentDialog.Page.Locator(".mud-popover-open .mud-list-item")
                .Filter(new() { HasTextString = RoleName })
                .First
                .ClickAsync();
            await agentDialog.Page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(roles.GetByTestId($"agent-edit-role-{RoleName}")).ToBeVisibleAsync();
            await SaveAsync(agentDialog);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => agent.Roles.Contains(RoleName),
                message: "The role was not given to the agent.");

            await AssertAgentSettingsShowRoleAsync(RoleName);

            // Act: rename the role the agent uses with the operation of the role, whose dialog says that its users follow.
            var rolePane = await OpenPaneAsync($"Authorization/{RoleName}");
            await RunOperationAsync(rolePane, "Rename", RenamedRoleName, "The roles, users and agents that name it are given the new name as well.");

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => !authorization.Roles.ContainsKey(RoleName) &&
                    authorization.Roles.TryGetValue(RenamedRoleName, out var role) && IsStateReadRule(role.Allow.SingleOrDefault()),
                message: "The role was not renamed in the role store.");
            await AsyncTestHelpers.WaitUntilAsync(
                () => agent.Roles.Contains(RenamedRoleName) && !agent.Roles.Contains(RoleName),
                message: "The agent was not given the new name of the role.");
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    try
                    {
                        return File.ReadAllText(agentFilePath).Contains(RenamedRoleName, StringComparison.Ordinal);
                    }
                    catch (IOException)
                    {
                        // The server writes the file in place, so a read can meet it held.
                        return false;
                    }
                },
                message: "The new name of the role was not written to the file of the agent.");
            // The roles were invalid for the agent between the rename of the role and of its roles; the configuration written
            // with the new name is applied after the file is written, which clears the status.
            await AsyncTestHelpers.WaitUntilAsync(
                () => agent.StatusMessage?.StartsWith("Invalid roles: ", StringComparison.Ordinal) != true,
                message: "The agent still reports invalid roles after the rename.");

            await AssertAgentSettingsShowRoleAsync(RenamedRoleName);

            // Act: delete the renamed role with the Delete button of its pane while the agent holds it.
            rolePane = await OpenPaneAsync($"Authorization/{RenamedRoleName}");
            await DeleteWithButtonAsync(rolePane, RenamedRoleName);

            // Assert: the role store refuses to delete a role in use.
            var refusal = rolePane.Page.GetByRole(AriaRole.Dialog);
            await Assertions.Expect(refusal).ToContainTextAsync("Delete Failed");
            await Assertions.Expect(refusal).ToContainTextAsync($"The role '{RenamedRoleName}' is used by users or agents; remove it from them first.");
            await refusal.GetByRole(AriaRole.Button, new() { Name = "OK", Exact = true }).ClickAsync();
            Assert.NotNull(roleStore.GetConcurrencyStamp(RenamedRoleName));

            // Act: take the role from the agent and delete it again.
            agent.Roles = originalRoles;
            rolePane = await OpenPaneAsync($"Authorization/{RenamedRoleName}");
            await DeleteWithButtonAsync(rolePane, RenamedRoleName);

            // Assert
            var panes = rolePane.Page.Locator("#scrollContainer [data-testid='subject-pane']");
            await Assertions.Expect(panes).ToHaveCountAsync(2);
            await AsyncTestHelpers.WaitUntilAsync(
                () => !authorization.Roles.ContainsKey(RenamedRoleName) && roleStore.GetConcurrencyStamp(RenamedRoleName) is null,
                message: "The deleted role was not removed from the role store.");
            isCompleted = true;
        }
        finally
        {
            try
            {
                agent.Roles = originalRoles;
                await File.WriteAllTextAsync(agentFilePath, originalAgentFile);

                // Refused while an agent names the role, so after the agent got its roles back.
                foreach (var name in new[] { RoleName, RenamedRoleName })
                {
                    if (roleStore.GetConcurrencyStamp(name) is { } stamp)
                    {
                        var result = await roleStore.DeleteRoleAsync(name, stamp, CancellationToken.None);
                        Assert.True(result.Succeeded, result.Error);
                    }
                }
            }
            catch (Exception exception) when (!isCompleted)
            {
                // The failure of the test itself is the one to report.
                _output.WriteLine($"Restoring the test data failed: {exception}");
            }
        }
    }

    private async Task AssertAgentSettingsShowRoleAsync(string roleName)
    {
        var agentPage = await _fixture.CreatePageAsync();
        await agentPage.GotoAsync($"{_fixture.ServerAddress}pages/{AgentPath}");
        var settingsTab = agentPage.GetByRole(AriaRole.Tab, new() { Name = "Settings" });
        await Assertions.Expect(settingsTab).ToBeVisibleAsync();
        await agentPage.WaitUntilInteractiveAsync();
        await settingsTab.ClickAsync();
        await Assertions.Expect(agentPage.GetByTestId("agent-settings-roles")).ToContainTextAsync(roleName);
        await Assertions.Expect(agentPage.GetByTestId("agent-settings-roles-error")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
    }

    private static Task<double> GetLayoutHeightAsync(ILocator element) =>
        element.EvaluateAsync<double>("element => element.offsetHeight");

    private static bool IsStateReadRule(PermissionRule? rule) =>
        rule is not null &&
        rule.Paths.SequenceEqual(new[] { FolderPattern }) &&
        rule.Types.SequenceEqual(new[] { TypeName }) &&
        rule.Tags.SequenceEqual(new[] { MemberTags.State }) &&
        rule.Actions.SequenceEqual(new[] { PermissionAction.Read });

    /// <summary>Opens the subject at the path in the subject browser and returns its pane, the last one.</summary>
    private async Task<ILocator> OpenPaneAsync(string path)
    {
        var page = await SubjectBrowserPage.OpenAsync(_fixture, path);

        // A path that does not resolve ends at an earlier pane, whose buttons belong to another subject.
        var paneCount = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length + 1;
        var panes = page.Locator("#scrollContainer [data-testid='subject-pane']");
        await Assertions.Expect(panes).ToHaveCountAsync(paneCount);
        return panes.Last;
    }

    /// <summary>Opens the edit dialog of the subject at the path from its pane in the subject browser.</summary>
    private async Task<ILocator> OpenEditDialogAsync(string path)
    {
        var pane = await OpenPaneAsync(path);
        var editButton = pane.GetByTestId("edit-subject-button");
        await Assertions.Expect(editButton).ToBeVisibleAsync();
        await editButton.ClickAsync();

        var dialog = pane.Page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        await dialog.WaitForDialogFocusAsync();
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
        await Assertions.Expect(operation).ToBeEnabledAsync();
        await operation.ClickAsync();

        var dialog = pane.Page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToContainTextAsync(title);
        await Assertions.Expect(dialog).ToContainTextAsync(description);
        await dialog.WaitForDialogFocusAsync();
        await dialog.Locator("input").First.FillAsync(text);
        var execute = dialog.GetByRole(AriaRole.Button, new() { Name = "Execute", Exact = true });
        await Assertions.Expect(execute).ToBeEnabledAsync();
        await execute.ClickAsync();

        // A failing operation opens its error dialog after this one closes; the wait for the file that follows then fails.
        await Assertions.Expect(dialog).ToHaveCountAsync(0);
    }

    /// <summary>
    /// Clicks the Delete button of the pane, which runs the delete operation of its subject, checks that the operation is
    /// not listed among the others and that the confirmation names the subject and carries the operation's description,
    /// and confirms; a refusal opens its error dialog afterwards.
    /// </summary>
    private static async Task DeleteWithButtonAsync(ILocator pane, string name)
    {
        var deleteButton = pane.GetByTestId("delete-subject-button");
        await Assertions.Expect(deleteButton).ToBeEnabledAsync();
        await Assertions.Expect(Method(pane, "Delete")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
        await deleteButton.ClickAsync();
        var confirmation = pane.Page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(confirmation).ToContainTextAsync($"Delete '{name}'?");
        await Assertions.Expect(confirmation).ToContainTextAsync("Refused while users or agents hold it");
        await confirmation.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
    }

    private static async Task SaveAsync(ILocator dialog)
    {
        var saveButton = dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });
        await Assertions.Expect(saveButton).ToBeEnabledAsync();
        await saveButton.ClickAsync();

        // The notification is the last step of the save, so everything it writes is written by now.
        await Assertions.Expect(dialog.Page.GetByText("Configuration saved.")).ToBeVisibleAsync();
    }

    private static string DescribeRoles(Authorization.Authorization authorization) =>
        string.Join("; ", authorization.Roles
            .Select(role => $"{role.Key}: allow {DescribeRules(role.Value.Allow)}, deny {DescribeRules(role.Value.Deny)}"));

    private static string DescribeRules(IReadOnlyList<PermissionRule> rules) =>
        "[" + string.Join(", ", rules.Select(rule =>
            $"paths=[{string.Join(",", rule.Paths)}] types=[{string.Join(",", rule.Types)}] members=[{string.Join(",", rule.Members)}] " +
            $"tags=[{string.Join(",", rule.Tags)}] actions=[{string.Join(",", rule.Actions)}]")) + "]";
}
