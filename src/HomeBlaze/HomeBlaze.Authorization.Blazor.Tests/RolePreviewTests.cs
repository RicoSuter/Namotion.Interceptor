using Bunit;
using HomeBlaze.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor.Authorization;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

public sealed class RolePreviewTests : BunitContext, IAsyncLifetime
{
    private const string Allowed = "allowed";
    private const string Denied = "denied";

    private readonly TestAuthorizationGraph _graph = TestAuthorizationGraph.Create("Kitchen");

    public RolePreviewTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_graph.Service);
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
    public async Task WhenASubjectIsChosen_ThenItIsShownWithAMatrixOfItsMembers()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Includes = [BuiltInRoles.ReaderName];
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices");

        // Assert
        var shown = component.Find("[data-testid='role-preview-shown']");
        Assert.Equal("true", shown.GetAttribute("data-shown"));
        Assert.Equal(
            ["Temperature", "Password", "Child", "Start", "Temperature@Minimum"],
            GetMemberNames(component).ToArray());

        Assert.Equal([Allowed, Denied, null, Allowed], GetCellStates(component, "Temperature"));
        Assert.Equal([Allowed, null, Denied, null], GetCellStates(component, "Start"));
        Assert.Equal([Denied, Denied, null, Denied], GetCellStates(component, "Password"));
        Assert.Equal([Allowed, Denied, null, Allowed], GetCellStates(component, "Temperature@Minimum"));
    }

    [Fact]
    public async Task WhenASubjectIsChosen_ThenEachMemberShowsThePathARuleMatchesItBy()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Includes = [BuiltInRoles.ReaderName];
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
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Members = ["Start"], Actions = [AuthorizationActions.Execute] }];
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");

        // Act
        await component.Find("[data-testid='role-preview-cell-Start-execute'] button").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal([Denied, null, Allowed, null], GetCellStates(component, "Start"));
        Assert.Equal("execute-without-read", component.Find("[data-testid='role-preview-cell-Start-execute']").GetAttribute("data-note"));
        Assert.Null(component.Find("[data-testid='role-preview-cell-Start-read']").GetAttribute("data-note"));
        Assert.Contains(
            "It cannot be used, as read is denied: A method is listed and run only when it is readable.",
            component.Find("[data-testid='role-preview-explanation']").TextContent);
    }

    [Fact]
    public async Task WhenACellIsDeniedByTheEditedRole_ThenItsExplanationNamesTheRule()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read, PermissionAction.Write] }];
        role.Deny = [new PermissionRule { Paths = ["/Devices/*"], Members = ["Temperature"], Actions = [PermissionAction.Write] }];
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");

        // Act
        await component.Find("[data-testid='role-preview-cell-Temperature-write'] button").ClickAsync(new MouseEventArgs());

        // Assert
        const string expected =
            "Denied by Kitchen: deny paths /Devices/*; members Temperature; actions write. " +
            "It overrides Kitchen: allow any member; actions read, write.";
        Assert.Equal($"Temperature, write: {expected}", component.Find("[data-testid='role-preview-explanation']").TextContent.Trim());
        Assert.Contains(component.FindComponents<MudTooltip>(), tooltip => tooltip.Instance.Text == expected);
    }

    [Fact]
    public async Task WhenASecretIsDeniedByASystemRule_ThenItsExplanationSaysSo()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");

        // Act
        await component.Find("[data-testid='role-preview-cell-Password-read'] button").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal(
            "Password, read: Denied by a system rule: deny tags secret; actions read, write, history. " +
            "It overrides Kitchen: allow any member; actions read.",
            component.Find("[data-testid='role-preview-explanation']").TextContent.Trim());
    }

    [Fact]
    public async Task WhenNoRoleAllowsAnAction_ThenItsExplanationSaysSo()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Members = ["Temperature"], Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");

        // Act
        await component.Find("[data-testid='role-preview-cell-Temperature-write'] button").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal("Temperature, write: No role allows it.", component.Find("[data-testid='role-preview-explanation']").TextContent.Trim());
    }

    [Fact]
    public async Task WhenAnUnsavedRuleChanges_ThenTheMatrixChangesAndTheRoleIsNotWritten()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var savedAllow = role.Allow;
        var component = RenderEditor(role);
        await ChoosePathAsync(component, "/Devices");
        Assert.Equal([Allowed, Denied, null, Denied], GetCellStates(component, "Temperature"));

        // Act
        await ActivateTabAsync(component, 0);
        var box = component.FindComponents<RuleCard>()[0].FindComponents<MudCheckBox<bool>>().Single(box => box.Instance.Label == "write");
        await component.InvokeAsync(() => box.Instance.ValueChanged.InvokeAsync(true));
        await ActivateTabAsync(component, 1);

        // Assert
        Assert.Equal("/Devices", component.Find("[data-testid='role-preview-path'] input").GetAttribute("value"));
        Assert.Equal([Allowed, Allowed, null, Denied], GetCellStates(component, "Temperature"));
        Assert.Same(savedAllow, role.Allow);
    }

    [Fact]
    public async Task WhenTheSubjectIsHidden_ThenTheUnreadableLinkIsShown()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        role.Deny = [new PermissionRule { Paths = ["/Devices/*"], Members = ["Child"], Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices/Child");

        // Assert
        var shown = component.Find("[data-testid='role-preview-shown']");
        Assert.Equal("false", shown.GetAttribute("data-shown"));
        Assert.Equal("Hidden: the property Child of /Devices is not readable.", shown.TextContent.Trim());
    }

    [Fact]
    public async Task WhenNoMemberIsReadable_ThenTheSubjectIsHidden()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Types = ["Unknown"], Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices");

        // Assert
        var shown = component.Find("[data-testid='role-preview-shown']");
        Assert.Equal("false", shown.GetAttribute("data-shown"));
        Assert.Equal("Hidden: no member is readable.", shown.TextContent.Trim());
    }

    [Fact]
    public async Task WhenTheRoleDoesNotCompile_ThenTheErrorIsShownInsteadOfTheMatrix()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Includes = ["Ghost"];
        var component = RenderEditor(role);

        // Act
        await ChoosePathAsync(component, "/Devices");

        // Assert
        Assert.Contains("Ghost", component.Find("[data-testid='role-preview-error']").TextContent);
        Assert.Empty(component.FindAll("[data-testid='role-preview-matrix']"));
    }

    [Fact]
    public async Task WhenNoSubjectHasThePath_ThenItIsSaid()
    {
        // Arrange
        var component = RenderEditor(_graph.GetRole("Kitchen"));

        // Act
        await ChoosePathAsync(component, "/Nowhere");

        // Assert
        Assert.NotEmpty(component.FindAll("[data-testid='role-preview-not-found']"));
        Assert.Empty(component.FindAll("[data-testid='role-preview-matrix']"));
    }

    private IRenderedComponent<RoleEditComponent> RenderEditor(Role role) =>
        Render<RoleEditComponent>(parameters => parameters.Add(component => component.Subject, role));

    private static async Task ChoosePathAsync(IRenderedComponent<RoleEditComponent> component, string path)
    {
        await ActivateTabAsync(component, 1);
        await component.Find("[data-testid='role-preview-path'] input").InputAsync(new ChangeEventArgs { Value = path });
    }

    private static Task ActivateTabAsync(IRenderedComponent<RoleEditComponent> component, int index)
    {
        var tabs = component.FindComponent<MudTabs>();
        return component.InvokeAsync(() => tabs.Instance.ActivatePanelAsync(index));
    }

    private static IReadOnlyList<string> GetMemberNames(IRenderedComponent<RoleEditComponent> component) =>
        component.FindAll("[data-testid='role-preview-matrix'] [data-member]").Select(row => row.GetAttribute("data-member")!).ToList();

    // In the order read, write, execute, history; null where the action does not apply.
    private static IReadOnlyList<string?> GetCellStates(IRenderedComponent<RoleEditComponent> component, string member) =>
        AuthorizationActions.All
            .Select(action => component.Find($"[data-testid='role-preview-cell-{member}-{action.Name}']").GetAttribute("data-decision"))
            .ToList();
}
