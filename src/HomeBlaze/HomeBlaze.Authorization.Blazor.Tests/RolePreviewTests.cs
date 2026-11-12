using Bunit;
using HomeBlaze.Services;
using HomeBlaze.Authorization.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

public sealed class RolePreviewTests : BunitContext, IAsyncLifetime
{
    private const string Allowed = "allowed";
    private const string Denied = "denied";
    private const int AllowTabIndex = 1;
    private const int PreviewTabIndex = 3;

    private readonly TestAuthorizationGraph _graph = TestAuthorizationGraph.Create("kitchen", "garden", "system");

    public RolePreviewTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_graph.Service);
        Services.AddSingleton<IRoleStore>(_graph.RoleStore);
        Services.AddSingleton<IUserPermissions>(UnrestrictedPermissions.Instance);
        Services.AddSingleton(_graph.PathResolver);

        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(Role), typeof(TestRoot), typeof(TestDevice)]);
        Services.AddSingleton(new SubjectTypeCatalog(new SubjectTypeRegistry(typeProvider)));

        Render<MudPopoverProvider>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _graph.Dispose();
    }

    [Fact]
    public async Task WhenTheRoleIncludesReader_ThenTheChosenSubjectIsReadableWithAMatrixOfItsMembers()
    {
        // Arrange
        var role = _graph.SetRole("kitchen", includes: [BuiltInRoles.ReaderName]);
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices");

        // Assert
        var readability = component.Find("[data-testid='role-preview-readable']");
        Assert.Equal("true", readability.GetAttribute("data-readable"));
        Assert.Equal(
            ["Temperature", "Password", "Child", "Start", "Temperature@Minimum"],
            GetMemberNames(component).ToArray());

        Assert.Equal([Allowed, Denied, null, Allowed, Denied], GetCellStates(component, "Temperature"));
        Assert.Equal([Allowed, null, Denied, null, Denied], GetCellStates(component, "Start"));
        Assert.Equal([Denied, Denied, null, Denied, Denied], GetCellStates(component, "Password"));
        Assert.Equal([Allowed, Denied, null, Allowed, Denied], GetCellStates(component, "Temperature@Minimum"));
    }

    [Fact]
    public async Task WhenASubjectIsChosen_ThenEachMemberShowsThePathARuleMatchesItBy()
    {
        // Arrange
        var role = _graph.SetRole("kitchen", includes: [BuiltInRoles.ReaderName]);
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices/Child");

        // Assert
        Assert.Equal("/Devices/Child/Temperature", component.Find("[data-testid='role-preview-path-Temperature']").TextContent.Trim());
        Assert.Equal("/Devices/Child/Start", component.Find("[data-testid='role-preview-path-Start']").TextContent.Trim());
        Assert.Equal("/Devices/Child/Minimum", component.Find("[data-testid='role-preview-path-Temperature@Minimum']").TextContent.Trim());
        Assert.Contains("role-preview-member-path", component.Find("[data-testid='role-preview-path-Start']").ClassName);
    }

    [Fact]
    public async Task WhenAMethodCanBeExecutedButNotRead_ThenItsExecuteCellSaysItCannotBeUsed()
    {
        // Arrange
        var role = _graph.SetRole("kitchen", allow: [new PermissionRule { Members = ["Start"], Actions = [AuthorizationActions.Execute] }]);
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");

        // Act
        await component.Find("[data-testid='role-preview-cell-Start-execute'] button").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal([Denied, null, Allowed, null, Denied], GetCellStates(component, "Start"));
        Assert.Equal("execute-without-read", component.Find("[data-testid='role-preview-cell-Start-execute']").GetAttribute("data-note"));
        Assert.Equal("Start, execute: allowed, cannot be used",
            component.Find("[data-testid='role-preview-cell-Start-execute'] button").GetAttribute("aria-label"));
        Assert.Equal("Start, read: denied", component.Find("[data-testid='role-preview-cell-Start-read'] button").GetAttribute("aria-label"));
        Assert.Null(component.Find("[data-testid='role-preview-cell-Start-read']").GetAttribute("data-note"));
        Assert.Contains(
            "It cannot be used, as read is denied: A method is listed and run only when it is readable.",
            component.Find("[data-testid='role-preview-explanation']").TextContent);
    }

    [Fact]
    public async Task WhenACellIsDeniedByTheEditedRole_ThenItsExplanationNamesTheRule()
    {
        // Arrange
        var role = _graph.SetRole("kitchen", allow: [new PermissionRule { Actions = [PermissionAction.Read, PermissionAction.Write] }], deny: [new PermissionRule { Paths = ["/Devices/*"], Members = ["Temperature"], Actions = [PermissionAction.Write] }]);
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");

        // Act
        await component.Find("[data-testid='role-preview-cell-Temperature-write'] button").ClickAsync(new MouseEventArgs());

        // Assert
        const string expected =
            "Denied by kitchen: deny write · paths: /Devices/* · members: Temperature. " +
            "It overrides kitchen: allow read, write.";
        Assert.Equal($"Temperature, write: {expected}", component.Find("[data-testid='role-preview-explanation']").TextContent.Trim());
        Assert.Contains(component.FindComponents<MudTooltip>(), tooltip => tooltip.Instance.Text == expected);
    }

    [Fact]
    public async Task WhenASecretIsDeniedByASystemRule_ThenItsExplanationSaysSo()
    {
        // Arrange
        var role = _graph.SetRole("kitchen", allow: [new PermissionRule { Actions = [PermissionAction.Read] }]);
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");

        // Act
        await component.Find("[data-testid='role-preview-cell-Password-read'] button").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal(
            "Password, read: Denied by a system rule: deny read, history · tags: secret. " +
            "It overrides kitchen: allow read.",
            component.Find("[data-testid='role-preview-explanation']").TextContent.Trim());
    }

    [Fact]
    public async Task WhenACellIsDeniedByARoleNamedSystem_ThenItsExplanationNamesTheRole()
    {
        // Arrange
        var role = _graph.SetRole("system", allow: [new PermissionRule { Actions = [PermissionAction.Read] }], deny: [new PermissionRule { Members = ["Temperature"], Actions = [PermissionAction.Read] }]);
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");

        // Act
        await component.Find("[data-testid='role-preview-cell-Temperature-read'] button").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal(
            "Temperature, read: Denied by system: deny read · members: Temperature. " +
            "It overrides system: allow read.",
            component.Find("[data-testid='role-preview-explanation']").TextContent.Trim());
    }

    [Fact]
    public async Task WhenNoRoleAllowsAnAction_ThenItsExplanationSaysSo()
    {
        // Arrange
        var role = _graph.SetRole("kitchen", allow: [new PermissionRule { Members = ["Temperature"], Actions = [PermissionAction.Read] }]);
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");

        // Act
        await component.Find("[data-testid='role-preview-cell-Temperature-write'] button").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal("Temperature, write: No role allows it.", component.Find("[data-testid='role-preview-explanation']").TextContent.Trim());
    }

    [Fact]
    public async Task WhenAnActionDoesNotApplyToAMember_ThenItsCellShowsADashLabeledAsNotApplyingUnderAStickyHeader()
    {
        // Arrange
        var role = _graph.SetRole("kitchen", includes: [BuiltInRoles.ReaderName]);
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices");

        // Assert
        var notApplicable = component.Find("[data-testid='role-preview-cell-Temperature-execute']");
        Assert.Null(notApplicable.GetAttribute("data-decision"));
        Assert.Equal("–", notApplicable.TextContent.Trim());
        var dash = notApplicable.QuerySelector(".role-preview-not-applicable");
        Assert.Equal("img", dash?.GetAttribute("role"));
        Assert.Equal("Does not apply", dash?.GetAttribute("aria-label"));
        Assert.Null(component.Find("[data-testid='role-preview-cell-Temperature-read']").QuerySelector(".role-preview-not-applicable"));
        Assert.Contains("mud-table-sticky-header", component.Find("[data-testid='role-preview-matrix']").ClassName);
    }

    [Fact]
    public async Task WhenAnUnsavedRuleChanges_ThenTheMatrixChangesAndTheRoleIsNotWritten()
    {
        // Arrange
        var role = _graph.SetRole("kitchen", allow: [new PermissionRule { Actions = [PermissionAction.Read] }]);
        var savedAllow = role.Allow;
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");
        Assert.Equal([Allowed, Denied, null, Denied, Denied], GetCellStates(component, "Temperature"));

        // Act
        await ActivateTabAsync(component, AllowTabIndex);
        var box = component.FindComponents<RuleCard>()[0].FindComponents<MudCheckBox<bool>>().Single(box => box.Instance.Label == "write");
        await component.InvokeAsync(() => box.Instance.ValueChanged.InvokeAsync(true));
        await ActivateTabAsync(component, PreviewTabIndex);

        // Assert
        Assert.Equal("/Devices", component.Find("[data-testid='role-preview-path'] input").GetAttribute("value"));
        Assert.Equal([Allowed, Allowed, null, Denied, Denied], GetCellStates(component, "Temperature"));
        Assert.Same(savedAllow, role.Allow);
    }

    [Fact]
    public async Task WhenThePreviewIsShown_ThenTheSubjectIsBrowsedWithAButtonTheKeyboardReaches()
    {
        // Arrange
        var component = RenderEditor(_graph.GetRole("kitchen"));

        // Act
        await ActivateTabAsync(component, PreviewTabIndex);

        // Assert
        var browse = component.Find("[data-testid='role-preview-path'] [data-testid='subject-path-field-browse']");
        Assert.Equal("Browse Subject", browse.GetAttribute("aria-label"));
        Assert.Null(browse.GetAttribute("tabindex"));
    }

    [Fact]
    public async Task WhenAnIncludedRoleIsSavedElsewhere_ThenTheMatrixFollowsIt()
    {
        // Arrange
        _graph.SetRole("garden", allow: [new PermissionRule { Members = ["Temperature"], Actions = [PermissionAction.Read] }]);
        var role = _graph.SetRole("kitchen", includes: ["garden"]);
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");

        // Act
        _graph.SetRole("garden", allow: [new PermissionRule { Members = ["Temperature"], Actions = [PermissionAction.Write] }]);

        // Assert
        await component.WaitForAssertionAsync(
            () => Assert.Equal([Denied, Allowed, null, Denied, Denied], GetCellStates(component, "Temperature")));
    }

    [Fact]
    public async Task WhenAGetterThrows_ThenTheMatrixListsTheMemberWithoutCallingIt()
    {
        // Arrange
        var isGetterThrowing = false;
        _graph.Devices.TryGetRegisteredSubject()!.AddProperty(
            "Broken", typeof(object), _ => isGetterThrowing ? throw new InvalidOperationException("The getter was called.") : null, null);
        isGetterThrowing = true;
        var role = _graph.SetRole("kitchen", includes: [BuiltInRoles.ReaderName]);
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices");

        // Assert
        Assert.Contains("Broken", GetMemberNames(component));
        Assert.Equal([Allowed, Denied, null, Allowed, Denied], GetCellStates(component, "Broken"));
    }

    [Fact]
    public async Task WhenTheSubjectIsHidden_ThenTheUnreadableLinkIsShown()
    {
        // Arrange
        var role = _graph.SetRole("kitchen", allow: [new PermissionRule { Actions = [PermissionAction.Read] }], deny: [new PermissionRule { Paths = ["/Devices/*"], Members = ["Child"], Actions = [PermissionAction.Read] }]);
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices/Child");

        // Assert
        var readability = component.Find("[data-testid='role-preview-readable']");
        Assert.Equal("false", readability.GetAttribute("data-readable"));
        Assert.Equal("Hidden: the property Child of /Devices is not readable.", readability.TextContent.Trim());
    }

    [Fact]
    public async Task WhenNoMemberIsReadable_ThenTheSubjectIsHidden()
    {
        // Arrange
        var role = _graph.SetRole("kitchen", allow: [new PermissionRule { Types = ["unknown"], Actions = [PermissionAction.Read] }]);
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices");

        // Assert
        var readability = component.Find("[data-testid='role-preview-readable']");
        Assert.Equal("false", readability.GetAttribute("data-readable"));
        Assert.Equal("Hidden: no member is readable.", readability.TextContent.Trim());
    }

    [Fact]
    public async Task WhenTheRoleDoesNotCompile_ThenTheErrorIsShownAboveTheTabsInsteadOfTheMatrix()
    {
        // Arrange
        var role = _graph.SetRole("kitchen", includes: ["ghost"]);
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices");

        // Assert
        Assert.Contains("ghost", Assert.Single(component.FindAll("[data-testid='role-edit-status']")).TextContent);
        Assert.Empty(component.FindAll("[data-testid='role-preview-matrix']"));
        Assert.Empty(component.FindAll("[data-testid='role-preview-not-found']"));
    }

    [Fact]
    public async Task WhenNoSubjectHasThePath_ThenItIsSaid()
    {
        // Arrange
        var component = RenderEditor(_graph.GetRole("kitchen"));

        // Act
        await ChoosePathAsync(component, "/Nowhere");

        // Assert
        Assert.NotEmpty(component.FindAll("[data-testid='role-preview-not-found']"));
        Assert.Empty(component.FindAll("[data-testid='role-preview-matrix']"));
    }

    [Fact]
    public async Task WhenThePreviewOpens_ThenItPreviewsAsAPerson()
    {
        // Arrange
        var component = RenderEditor(_graph.GetRole("kitchen"));

        // Act
        await ActivateTabAsync(component, 1);

        // Assert
        Assert.Equal(PrincipalKind.Person, component.FindComponent<MudSelect<PrincipalKind>>().Instance.GetState(select => select.Value));
    }

    [Theory]
    [InlineData(PrincipalKind.Person, Allowed)]
    [InlineData(PrincipalKind.NonHuman, Denied)]
    [InlineData(PrincipalKind.Anonymous, Denied)]
    public async Task WhenTheRoleIsPreviewedAsAKindOfPrincipal_ThenAdministerFollowsTheSystemRulesOfThatKind(PrincipalKind kind, string expectedAdminister)
    {
        // Arrange
        var role = _graph.SetRole("kitchen", includes: [BuiltInRoles.EverythingName]);
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");

        // Act
        await ChooseKindAsync(component, kind);

        // Assert
        var members = GetMemberNames(component);
        Assert.NotEmpty(members);
        Assert.All(members, member => Assert.Equal(expectedAdminister, GetCellStates(component, member)[4]));
    }

    [Fact]
    public async Task WhenDenyRulesMatchMembersOfTheSubject_ThenEachShowsHowManyItMatches()
    {
        // Arrange
        var role = _graph.SetRole(
            "kitchen",
            allow: [new PermissionRule { Actions = [PermissionAction.Read] }],
            deny:
            [
                new PermissionRule { Paths = ["/Devices/*"], Members = ["Temperature", "Start"], Actions = [PermissionAction.Write, AuthorizationActions.Execute] },
                new PermissionRule { Paths = ["/Devices/Child/Temperature"], Actions = [PermissionAction.Read] },
                new PermissionRule { Paths = ["/Devices/*"], Members = ["Temperature", "Start"], Actions = [AuthorizationActions.Execute] }
            ]);
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices");

        // Assert
        var first = component.Find("[data-testid='role-preview-deny-0']");
        Assert.Equal("2", first.GetAttribute("data-count"));
        Assert.Equal("Deny rule 1 (deny write, execute · paths: /Devices/* · members: Temperature, Start) matches 2 members of this subject.", first.TextContent.Trim());
        var second = component.Find("[data-testid='role-preview-deny-1']");
        Assert.Equal("0", second.GetAttribute("data-count"));
        Assert.Equal("false", second.GetAttribute("data-matches-nothing"));
        Assert.Equal("Deny rule 2 (deny read · paths: /Devices/Child/Temperature) matches no member of this subject.", second.TextContent.Trim());
        // Execute applies to methods alone, as in the matrix.
        Assert.Equal("1", component.Find("[data-testid='role-preview-deny-2']").GetAttribute("data-count"));
    }

    [Fact]
    public async Task WhenADenyRuleMatchesNoMemberAnywhere_ThenItIsWarnedThatItMatchesNothing()
    {
        // Arrange
        var role = _graph.SetRole(
            "kitchen",
            allow: [new PermissionRule { Actions = [PermissionAction.Read] }],
            deny: [new PermissionRule { Paths = ["/Kitchen/**"], Actions = [PermissionAction.Read] }]);
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices");

        // Assert
        var hint = component.Find("[data-testid='role-preview-deny-0']");
        Assert.Equal("0", hint.GetAttribute("data-count"));
        Assert.Equal("true", hint.GetAttribute("data-matches-nothing"));
        Assert.Contains("mud-alert-text-warning", hint.ClassName);
        Assert.Equal("Deny rule 1 (deny read · paths: /Kitchen/**) matches nothing: no member of any subject, so it denies nothing.", hint.TextContent.Trim());
    }

    private IRenderedComponent<RoleEditComponent> RenderEditor(Role role) =>
        Render<RoleEditComponent>(parameters => parameters.Add(component => component.Subject, role));

    private static async Task ChoosePathAsync(IRenderedComponent<RoleEditComponent> component, string path)
    {
        await ActivateTabAsync(component, PreviewTabIndex);
        await component.Find("[data-testid='role-preview-path'] input").InputAsync(new ChangeEventArgs { Value = path });
    }

    private static async Task ChooseKindAsync(IRenderedComponent<RoleEditComponent> component, PrincipalKind kind)
    {
        var select = component.FindComponent<MudSelect<PrincipalKind>>();
        await component.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(kind));
    }

    private static Task ActivateTabAsync(IRenderedComponent<RoleEditComponent> component, int index)
    {
        var tabs = component.FindComponent<MudTabs>();
        return component.InvokeAsync(() => tabs.Instance.ActivatePanelAsync(index));
    }

    private static IReadOnlyList<string> GetMemberNames(IRenderedComponent<RoleEditComponent> component) =>
        component.FindAll("[data-testid='role-preview-matrix'] [data-member]").Select(row => row.GetAttribute("data-member")!).ToList();

    // In the order read, write, execute, history, administer; null where the action does not apply.
    private static IReadOnlyList<string?> GetCellStates(IRenderedComponent<RoleEditComponent> component, string member) =>
        AuthorizationActions.All
            .Select(action => component.Find($"[data-testid='role-preview-cell-{member}-{action.Name}']").GetAttribute("data-decision"))
            .ToList();
}
