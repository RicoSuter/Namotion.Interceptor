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
/// E2E tests for editing the custom roles in the dialogs of the Authorization subject and its roles, and for giving a
/// role to an agent.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class AuthorizationRoleTests
{
    private const int Timeout = 30000;
    private const string AgentPath = "Agents/TestAgent";
    private const string RoleName = "StateReader";

    private readonly PlaywrightFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AuthorizationRoleTests(PlaywrightFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task WhenARoleIsCreatedInTheUiAndGivenToAnAgent_ThenTheAgentShowsItAndTheFileHoldsItsRule()
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

            // Act: add the role in the dialog of the Authorization subject.
            var authorizationDialog = await OpenEditDialogAsync("Authorization");
            await authorizationDialog.GetByTestId("authorization-add-role").ClickAsync();
            await authorizationDialog.GetByTestId("authorization-role-name").Locator("input").FillAsync(RoleName);
            var confirmName = authorizationDialog.GetByTestId("authorization-role-name-confirm");
            await Assertions.Expect(confirmName).ToBeEnabledAsync(new() { Timeout = Timeout });
            await confirmName.ClickAsync();
            await Assertions.Expect(authorizationDialog.GetByTestId($"authorization-role-row-{RoleName}")).ToContainTextAsync("(new)");
            await SaveAsync(authorizationDialog);

            // Assert
            await WaitForAuthorizationFileAsync(
                authorizationFilePath,
                roles => roles[RoleName] is not null,
                "The added role was not written to Authorization.json.");

            // Act: add an allow rule in the dialog of the role.
            var roleDialog = await OpenEditDialogAsync($"Authorization/{RoleName}");
            await Assertions.Expect(roleDialog.GetByTestId("role-edit-name")).ToHaveTextAsync(RoleName, new() { Timeout = Timeout });
            await roleDialog.GetByTestId("role-edit-add-allow").ClickAsync();
            var rule = roleDialog.GetByTestId("role-edit-allow-rule-0");
            await rule.GetByTestId("rule-tags").Locator("input").FillAsync(MemberTags.State);
            await rule.Page.Locator(".mud-popover-open .mud-list-item")
                .Filter(new() { HasTextString = MemberTags.State })
                .First
                .ClickAsync();
            await Assertions.Expect(rule.GetByTestId("rule-tags-chip")).ToHaveTextAsync(MemberTags.State, new() { Timeout = Timeout });
            await rule.GetByTestId("rule-action-read").CheckAsync();
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
        JsonNode.DeepEquals(rule["tags"], new JsonArray(MemberTags.State)) &&
        JsonNode.DeepEquals(rule["actions"], new JsonArray("read"));

    /// <summary>Opens the edit dialog of the subject at the path from its pane in the subject browser.</summary>
    private async Task<ILocator> OpenEditDialogAsync(string path)
    {
        var page = await SubjectBrowserPage.OpenAsync(_fixture, path);

        // A path that does not resolve ends at an earlier pane, whose edit button would open the dialog of another subject.
        var paneCount = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length + 1;
        await Assertions.Expect(page.Locator("#scrollContainer [data-testid='subject-pane']"))
            .ToHaveCountAsync(paneCount, new() { Timeout = Timeout });

        var editButton = page.Locator("#scrollContainer [data-testid='edit-subject-button']").Last;
        await Assertions.Expect(editButton).ToBeVisibleAsync(new() { Timeout = Timeout });
        await editButton.ClickAsync();

        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = Timeout });
        return dialog;
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
