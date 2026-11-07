using HomeBlaze.Authentication;
using HomeBlaze.Authorization;
using HomeBlaze.Components.Abstractions.Access;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Samples;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for what people see and may do in the browser with the roles they hold: each test adds its people as the
/// seeded administrator and signs them in through the login page in a browser context of their own.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class PeopleAuthorizationTests
{
    private const int Timeout = 30000;
    private const string ControlsPage = "Controls.md";
    private const string MotorName = "ctlmotor";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly PlaywrightFixture _fixture;

    public PeopleAuthorizationTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAnOperatorUsesASwitch_ThenItToggles_AndTheEditButtonIsAbsent()
    {
        // Arrange
        var userName = TestNames.Create("operator");
        await _fixture.AddPersonAsync(userName, BuiltInRoles.OperatorName);
        try
        {
            ResetMotor();
            var page = await OpenDemoPageAsync(SignedInAs.User(userName), ControlsPage);
            var powerSwitch = GetSwitch(page, "Power");
            await Assertions.Expect(powerSwitch).ToBeCheckedAsync(new() { Timeout = Timeout });

            // Act
            await powerSwitch.ClickAsync();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => !GetMotor().IsPowered, WaitTimeout, message: "The switch did not power the motor off");
            await Assertions.Expect(powerSwitch).Not.ToBeCheckedAsync(new() { Timeout = Timeout });
            await Assertions.Expect(page.GetByTestId("page-head")).ToBeAttachedAsync();
            await Assertions.Expect(page.GetByTestId("edit-mode-menu")).ToHaveCountAsync(0);
        }
        finally
        {
            ResetMotor();
            await _fixture.RemovePersonAsync(userName);
        }
    }

    [Fact]
    public async Task WhenAnOperatorOpensControlsOfConfigurationProperties_ThenTheyAreDisabledWithTheReason()
    {
        // Arrange
        var userName = TestNames.Create("operator");
        await _fixture.AddPersonAsync(userName, BuiltInRoles.OperatorName);
        try
        {
            var originalContent = await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(ControlsPage));

            // Act
            var page = await OpenDemoPageAsync(SignedInAs.User(userName), ControlsPage);

            // Assert: configuration is changed only through administration, which an operator may not do.
            var outlineSwitch = GetSwitch(page, "Card outline");
            await Assertions.Expect(outlineSwitch).ToBeDisabledAsync(new() { Timeout = Timeout });
            await Assertions.Expect(page.GetByTestId("switch").Filter(new() { Has = outlineSwitch }))
                .ToHaveAttributeAsync("title", AccessDeniedTexts.Tooltip);
            var speedSlider = page.GetByRole(AriaRole.Slider, new() { Name = "Target speed", Exact = true });
            await Assertions.Expect(speedSlider).ToBeDisabledAsync(new() { Timeout = Timeout });
            await Assertions.Expect(page.GetByTestId("slider").Filter(new() { Has = speedSlider }))
                .ToHaveAttributeAsync("title", AccessDeniedTexts.Tooltip);

            // Act: try the disabled controls anyway.
            await outlineSwitch.ClickAsync(new() { Force = true });
            await speedSlider.ClickAsync(new() { Force = true });

            // Assert: nothing was written.
            await Assertions.Expect(outlineSwitch).ToBeDisabledAsync();
            await Assertions.Expect(speedSlider).ToBeDisabledAsync();
            Assert.Equal(originalContent, await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(ControlsPage)));
        }
        finally
        {
            await _fixture.RemovePersonAsync(userName);
        }
    }

    [Fact]
    public async Task WhenAFullAdministratorOpensAPage_ThenTheyCanEnterEditMode()
    {
        // Arrange
        var page = await OpenDemoPageAsync(SignedInAs.Admin, ControlsPage);

        // Act
        await page.SelectEditModeAsync("Source");

        // Assert
        await Assertions.Expect(page.GetByTestId("editor-bar")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAReaderUsesASwitch_ThenItIsDisabled()
    {
        // Arrange
        var userName = TestNames.Create("reader");
        await _fixture.AddPersonAsync(userName, BuiltInRoles.ReaderName);
        try
        {
            ResetMotor();

            // Act
            var page = await OpenDemoPageAsync(SignedInAs.User(userName), ControlsPage);

            // Assert
            var powerSwitch = GetSwitch(page, "Power");
            await Assertions.Expect(powerSwitch).ToBeCheckedAsync(new() { Timeout = Timeout });
            await Assertions.Expect(powerSwitch).ToBeDisabledAsync(new() { Timeout = Timeout });
            Assert.True(GetMotor().IsPowered);
        }
        finally
        {
            await _fixture.RemovePersonAsync(userName);
        }
    }

    [Fact]
    public async Task WhenAnonymousHasAGuestRole_ThenASignedOutVisitorSeesTheAllowedPageOnly()
    {
        // Arrange: the guest reads the controls page and the folders that lead to it, and nothing else.
        var roleName = TestNames.Create("guest");
        await AddRoleAsync(new AuthorizationRole
        {
            Name = roleName,
            Allow =
            [
                new PermissionRule { Paths = [$"/Demo/{ControlsPage}/**"], Actions = [PermissionAction.Read] },
                new PermissionRule { Paths = ["/Children", "/Demo/Children"], Actions = [PermissionAction.Read] }
            ]
        });
        await SetAnonymousRolesAsync([roleName]);
        try
        {
            // Act
            var page = await _fixture.CreatePageAsync(SignedInAs.None);
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ControlsPage}");

            // Assert
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Controls Demo" })).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(page.GetByTestId("user-menu-sign-in")).ToBeVisibleAsync(new() { Timeout = Timeout });

            // Act
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Cards.md");

            // Assert
            await Assertions.Expect(page.GetByText("Subject not found at path: Demo/Cards.md")).ToBeVisibleAsync(new() { Timeout = Timeout });
            Assert.Contains("/pages/Demo/Cards.md", page.Url, StringComparison.Ordinal);
        }
        finally
        {
            await SetAnonymousRolesAsync([]);
            await RemoveRoleAsync(roleName);
        }
    }

    [Fact]
    public async Task WhenAUserManagerAddsAUser_ThenTheyCanAssignOnlyRolesTheyHold()
    {
        // Arrange: the manager reads everything and administers only the users.
        var roleName = TestNames.Create("user-manager");
        var managerName = TestNames.Create("manager");
        var addedName = TestNames.Create("added");
        await AddRoleAsync(new AuthorizationRole
        {
            Name = roleName,
            Includes = [BuiltInRoles.ReaderName],
            Allow = [new PermissionRule { Paths = ["/Authorization/AddUser", "/Authorization/Users/**"], Actions = [AuthorizationActions.Administer] }]
        });
        await _fixture.AddPersonAsync(managerName, roleName);
        var userStore = _fixture.ServerServices.GetRequiredService<UserStore>();
        try
        {
            var pane = await OpenAuthorizationPaneAsync(SignedInAs.User(managerName));

            // Act: open the role picker of a new user.
            var dialog = await SubjectBrowserPage.OpenOperationDialogAsync(pane, "Add User");
            await Assertions.Expect(dialog).ToContainTextAsync("Only the roles you hold can be assigned.");
            await dialog.Locator("input").First.FillAsync(addedName);
            await dialog.Locator(".mud-select").First.ClickAsync();
            var options = pane.Page.Locator(".mud-popover-open .mud-list-item");
            await Assertions.Expect(options.First).ToBeVisibleAsync(new() { Timeout = Timeout });

            // Assert: only the roles the manager holds are offered.
            var offered = (await options.AllInnerTextsAsync()).Select(text => text.Trim()).ToList();
            Assert.Contains(BuiltInRoles.ReaderName, offered);
            Assert.Contains(roleName, offered);
            Assert.DoesNotContain(BuiltInRoles.OperatorName, offered);
            Assert.DoesNotContain(BuiltInRoles.EverythingName, offered);

            // Act: add the user with a role the manager holds.
            await options.Filter(new() { HasTextString = BuiltInRoles.ReaderName }).First.ClickAsync();
            await Assertions.Expect(dialog.GetByTestId($"parameter-role-{BuiltInRoles.ReaderName}")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await pane.Page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(options).ToHaveCountAsync(0, new() { Timeout = Timeout });
            await SubjectBrowserPage.ExecuteAsync(dialog);

            // Assert
            await Assertions.Expect(dialog.Page.GetByTestId("one-time-secret-value")).Not.ToBeEmptyAsync(new() { Timeout = Timeout });
            Assert.Equal(new[] { BuiltInRoles.ReaderName }, userStore.GetRoles(userStore.GetUserId(addedName)!));

            var managerAdministration = userStore.CreateAdministration(userStore.GetUserId(managerName));
            var refused = await managerAdministration.AddUserAsync(TestNames.Create("refused"), null, [BuiltInRoles.OperatorName], CancellationToken.None);
            Assert.False(refused.Succeeded);
        }
        finally
        {
            if (userStore.GetUserId(addedName) is not null)
            {
                await _fixture.RemovePersonAsync(addedName);
            }

            await _fixture.RemovePersonAsync(managerName);
            await RemoveRoleAsync(roleName);
        }
    }

    [Fact]
    public async Task WhenAWidgetIsBoundToAHiddenPath_ThenTheViewerSeesNoValue()
    {
        // Arrange: a reader of everything but the target speed of the motor of the values page.
        var roleName = TestNames.Create("no-target-speed");
        var userName = TestNames.Create("viewer");
        await AddRoleAsync(new AuthorizationRole
        {
            Name = roleName,
            Includes = [BuiltInRoles.ReaderName],
            Deny = [new PermissionRule { Paths = ["/Demo/Values.md/valuemotor/TargetSpeed"], Actions = [PermissionAction.Read] }]
        });
        await _fixture.AddPersonAsync(userName, roleName);
        try
        {
            // Act
            var page = await OpenDemoPageAsync(SignedInAs.User(userName), "Values.md");

            // Assert: the hidden value renders like a path that leads nowhere, next to one the viewer reads.
            var hiddenRow = GetValueRow(page, "Soll");
            await Assertions.Expect(hiddenRow.GetByTestId("value-missing")).ToHaveTextAsync("–", new() { Timeout = Timeout });
            await Assertions.Expect(hiddenRow.GetByTestId("value-text")).ToHaveCountAsync(0);
            await Assertions.Expect(GetValueRow(page, "Ist").GetByTestId("value-text")).ToBeVisibleAsync(new() { Timeout = Timeout });
        }
        finally
        {
            await _fixture.RemovePersonAsync(userName);
            await RemoveRoleAsync(roleName);
        }
    }

    [Fact]
    public async Task WhenAPersonWithoutRolesSignsIn_ThenHomeShowsTheNoRolesMessage()
    {
        // Arrange
        var userName = TestNames.Create("no-roles");
        await _fixture.AddPersonAsync(userName);
        try
        {
            // Act
            var page = await _fixture.CreatePageAsync(SignedInAs.User(userName));

            // Assert
            await Assertions.Expect(page.GetByTestId("no-roles")).ToHaveTextAsync("No roles yet. Ask an administrator.", new() { Timeout = Timeout });
            Assert.Equal("/", new Uri(page.Url).AbsolutePath);
        }
        finally
        {
            await _fixture.RemovePersonAsync(userName);
        }
    }

    private Motor GetMotor() =>
        (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[ControlsPage]).Children[MotorName];

    private void ResetMotor() => GetMotor().IsPowered = true;

    private static ILocator GetSwitch(IPage page, string label) =>
        page.GetByRole(AriaRole.Switch, new() { Name = label, Exact = true });

    private static ILocator GetValueRow(IPage page, string label) =>
        page.GetByTestId("value-row").Filter(new() { Has = page.GetByTestId("value-label").GetByText(label, new() { Exact = true }) });

    private async Task<IPage> OpenDemoPageAsync(SignedInAs user, string pageName)
    {
        var page = await _fixture.CreatePageAsync(user);
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{pageName}");

        // The controls are prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }

    private async Task<ILocator> OpenAuthorizationPaneAsync(SignedInAs user)
    {
        var page = await _fixture.CreatePageAsync(user);
        await page.GotoAsync($"{_fixture.ServerAddress}browser/Authorization");
        await page.WaitForFunctionAsync(SubjectBrowserPage.IsInteractive, null, new() { Timeout = Timeout });

        // A path that does not resolve ends at an earlier pane, whose operations belong to another subject.
        var panes = page.Locator("#scrollContainer [data-testid='subject-pane']");
        await Assertions.Expect(panes).ToHaveCountAsync(2, new() { Timeout = Timeout });
        return panes.Last;
    }

    private async Task AddRoleAsync(AuthorizationRole role)
    {
        var result = await _fixture.ServerServices.GetRequiredService<IRoleStore>().SaveRoleAsync(null, role, null, CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
    }

    private async Task RemoveRoleAsync(string roleName)
    {
        var roleStore = _fixture.ServerServices.GetRequiredService<IRoleStore>();
        var result = await roleStore.DeleteRoleAsync(roleName, roleStore.GetConcurrencyStamp(roleName), CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
    }

    private async Task SetAnonymousRolesAsync(IReadOnlyCollection<string> roles)
    {
        var result = await _fixture.CreateAdministration().SetRolesAsync(NameRules.AnonymousUserName, roles, CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
    }
}
