using Bunit;
using HomeBlaze.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

public sealed class AuthorizationEditComponentTests : BunitContext, IAsyncLifetime
{
    private readonly TestAuthorizationGraph _graph = TestAuthorizationGraph.Create("Kitchen", "Garden");

    public AuthorizationEditComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_graph.Service);

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
    public void WhenTheAuthorizationIsLoaded_ThenItsRolesAreListedWithTheirCountsAndTheBuiltInRolesBelow()
    {
        // Arrange
        var kitchen = _graph.GetRole("Kitchen");
        kitchen.Allow =
        [
            new PermissionRule { Actions = [PermissionAction.Read] },
            new PermissionRule { Actions = [PermissionAction.Write] }
        ];
        kitchen.Deny = [new PermissionRule { Tags = [MemberTags.Secret], Actions = [PermissionAction.Read] }];
        _graph.Service.SetPrincipalRoles(new object(), ["kitchen"]);

        // Act
        var component = RenderEditor(_graph.Authorization);

        // Assert
        Assert.Equal(["Kitchen", "Garden"], GetRoleNames(component));
        var row = component.Find("[data-testid='authorization-role-row-Kitchen']");
        Assert.Equal("2 allow rules, 1 deny rule", row.QuerySelector("[data-testid='authorization-role-rules']")!.TextContent.Trim());
        Assert.Equal("used by 1 agent", row.QuerySelector("[data-testid='authorization-role-used-by']")!.TextContent.Trim());
        Assert.Equal("ok", row.QuerySelector("[data-testid='authorization-role-status']")!.GetAttribute("data-status"));

        Assert.Equal(3, component.FindAll("[data-testid^='authorization-builtin-role-']").Count);
        Assert.Contains("Reads every member", component.Find($"[data-testid='authorization-builtin-role-{BuiltInRoles.ReaderName}']").TextContent);
        Assert.Empty(component.FindAll("[data-testid='authorization-status']"));
        Assert.True(component.Instance.IsValid);
        Assert.False(component.Instance.IsDirty);
    }

    [Fact]
    public void WhenSeveralAuthorizationsExist_ThenTheStatusAndTheErrorOfEachRoleAreShown()
    {
        // Arrange
        _graph.Root.Children = new Dictionary<string, IInterceptorSubject>(_graph.Root.Children)
        {
            ["Other"] = new Authorization()
        };

        // Act
        var component = RenderEditor(_graph.Authorization);

        // Assert
        Assert.Contains("Only one Authorization subject is allowed", component.Find("[data-testid='authorization-status']").TextContent);
        var status = component.Find("[data-testid='authorization-role-row-Kitchen'] [data-testid='authorization-role-status']");
        Assert.Equal("error", status.GetAttribute("data-status"));
    }

    [Fact]
    public async Task WhenARoleIsAddedAndSaved_ThenANewDictionaryHoldsTheExistingRolesAndANewEmptyOne()
    {
        // Arrange
        var authorization = _graph.Authorization;
        var previousRoles = authorization.Roles;
        var kitchen = _graph.GetRole("Kitchen");
        var component = RenderEditor(authorization);

        // Act
        await AddRoleAsync(component, " Pantry ");

        // Assert
        Assert.Equal(["Kitchen", "Garden", "Pantry"], GetRoleNames(component));
        Assert.True(component.Instance.IsDirty);
        Assert.Same(previousRoles, authorization.Roles);

        // Act
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.NotSame(previousRoles, authorization.Roles);
        Assert.Equal(["Kitchen", "Garden", "Pantry"], authorization.Roles.Keys);
        Assert.Same(kitchen, authorization.Roles["kitchen"]);
        Assert.Empty(authorization.Roles["Pantry"].Allow);
        Assert.False(component.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenARoleIsRenamedAndSaved_ThenTheSameRoleWithItsRulesIsHeldUnderTheNewName()
    {
        // Arrange
        var kitchen = _graph.GetRole("Kitchen");
        var rule = new PermissionRule { Paths = ["/Devices/**"], Actions = [PermissionAction.Read] };
        kitchen.Allow = [rule];
        var component = RenderEditor(_graph.Authorization);

        // Act
        await ClickAsync(component, "authorization-role-rename-Kitchen");
        Assert.Equal("Kitchen", component.Find("[data-testid='authorization-role-name'] input").GetAttribute("value"));
        await TypeNameAsync(component, "Pantry");
        await ClickAsync(component, "authorization-role-name-confirm");
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal(["Pantry", "Garden"], _graph.Authorization.Roles.Keys);
        var pantry = _graph.Authorization.Roles["Pantry"];
        Assert.Same(kitchen, pantry);
        Assert.Equal([rule], pantry.Allow);
        Assert.Equal(["Pantry"], pantry.TryGetRegisteredSubject()!.Parents.Select(parent => parent.Index));
    }

    [Fact]
    public async Task WhenARoleIsRenamedToADifferentCase_ThenTheNameIsAccepted()
    {
        // Arrange
        var component = RenderEditor(_graph.Authorization);

        // Act
        await ClickAsync(component, "authorization-role-rename-Kitchen");
        await TypeNameAsync(component, "KITCHEN");

        // Assert
        Assert.False(component.Find("[data-testid='authorization-role-name-confirm']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task WhenAnUnusedRoleIsRemovedAndSaved_ThenItIsLeftOut()
    {
        // Arrange
        var component = RenderEditor(_graph.Authorization);

        // Act
        await ClickAsync(component, "authorization-role-remove-Garden");
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal(["Kitchen"], GetRoleNames(component));
        Assert.Equal(["Kitchen"], _graph.Authorization.Roles.Keys);
    }

    [Fact]
    public async Task WhenAUsedRoleIsRemoved_ThenAConfirmationNamesTheNumberOfAgents()
    {
        // Arrange
        _graph.Service.SetPrincipalRoles(new object(), ["Garden"]);
        _graph.Service.SetPrincipalRoles(new object(), ["Garden", "Kitchen"]);
        var component = RenderEditor(_graph.Authorization);

        // Act
        await ClickAsync(component, "authorization-role-remove-Garden");

        // Assert
        Assert.Contains(
            "2 agents use this role. After saving, they are denied everything and show \"Invalid roles\" until they are given another role.",
            component.Find("[data-testid='authorization-role-remove-warning']").TextContent);
        Assert.Equal(["Kitchen", "Garden"], GetRoleNames(component));

        // Act
        await ClickAsync(component, "authorization-role-remove-confirm");

        // Assert
        Assert.Equal(["Kitchen"], GetRoleNames(component));
        Assert.Empty(component.FindAll("[data-testid='authorization-role-remove-warning']"));
    }

    [Fact]
    public async Task WhenTheRemovalOfAUsedRoleIsCanceled_ThenTheRoleStays()
    {
        // Arrange
        _graph.Service.SetPrincipalRoles(new object(), ["Garden"]);
        var component = RenderEditor(_graph.Authorization);
        await ClickAsync(component, "authorization-role-remove-Garden");

        // Act
        await ClickAsync(component, "authorization-role-remove-cancel");

        // Assert
        Assert.Equal(["Kitchen", "Garden"], GetRoleNames(component));
        Assert.False(component.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenAUsedRoleIsRenamed_ThenAWarningNamesTheNumberOfAgents()
    {
        // Arrange
        _graph.Service.SetPrincipalRoles(new object(), ["Kitchen"]);
        var component = RenderEditor(_graph.Authorization);

        // Act
        await ClickAsync(component, "authorization-role-rename-Kitchen");

        // Assert
        Assert.Contains(
            "1 agent uses this role under its old name. After saving, they are denied everything and show \"Invalid roles\" until they are given the new name or another role.",
            component.Find("[data-testid='authorization-role-rename-warning']").TextContent);
    }

    [Theory]
    [InlineData("garden", "A role with this name exists already.")]
    [InlineData("reader", "The name 'reader' is reserved for a built-in role.")]
    [InlineData("SYSTEM", "The name 'SYSTEM' is reserved for the system rules.")]
    [InlineData("  ", "Enter a name.")]
    [InlineData("Kitchen/Lights", "The name 'Kitchen/Lights' cannot contain '/', '[' or ']', because it is part of the role's path.")]
    [InlineData("Lights[0]", "The name 'Lights[0]' cannot contain '/', '[' or ']', because it is part of the role's path.")]
    public async Task WhenTheNameIsInvalid_ThenItsErrorIsShownAndItCannotBeAdded(string name, string error)
    {
        // Arrange
        var component = RenderEditor(_graph.Authorization);
        await ClickAsync(component, "authorization-add-role");

        // Act
        await TypeNameAsync(component, name);

        // Assert
        Assert.Contains(error, component.Markup);
        Assert.True(component.Find("[data-testid='authorization-role-name-confirm']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task WhenAnInvalidNameIsTyped_ThenTheAuthorizationIsInvalidUntilTheFieldIsCanceled()
    {
        // Arrange
        var component = RenderEditor(_graph.Authorization);
        var validity = new List<bool>();
        component.Instance.IsValidChanged += validity.Add;
        await ClickAsync(component, "authorization-add-role");

        // Act
        await TypeNameAsync(component, "Operator");

        // Assert
        Assert.False(component.Instance.IsValid);

        // Act
        await ClickAsync(component, "authorization-role-name-cancel");

        // Assert
        Assert.True(component.Instance.IsValid);
        Assert.Equal([false, true], validity);
        Assert.Empty(component.FindAll("[data-testid='authorization-role-name']"));
    }

    [Fact]
    public async Task WhenAnInvalidNameIsTyped_ThenSavingThrowsAndChangesNothing()
    {
        // Arrange
        var previousRoles = _graph.Authorization.Roles;
        var component = RenderEditor(_graph.Authorization);
        await ClickAsync(component, "authorization-add-role");
        await TypeNameAsync(component, "Operator");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));
        Assert.Same(previousRoles, _graph.Authorization.Roles);
    }

    [Fact]
    public async Task WhenAValidNameIsTypedButNotConfirmed_ThenSavingAddsIt()
    {
        // Arrange
        var component = RenderEditor(_graph.Authorization);
        await ClickAsync(component, "authorization-add-role");
        await TypeNameAsync(component, "Pantry");

        // Act
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal(["Kitchen", "Garden", "Pantry"], _graph.Authorization.Roles.Keys);
    }

    [Fact]
    public async Task WhenCreating_ThenTheListStartsEmptyAndSavingAssignsTheAddedRoles()
    {
        // Arrange
        var authorization = new Authorization();
        var component = Render<AuthorizationEditComponent>(parameters => parameters
            .Add(editor => editor.Subject, authorization)
            .Add(editor => editor.IsCreating, true));

        // Act
        await AddRoleAsync(component, "Kitchen");
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal(["Kitchen"], authorization.Roles.Keys);
        Assert.True(authorization.Roles.ContainsKey("KITCHEN"));
    }

    private IRenderedComponent<AuthorizationEditComponent> RenderEditor(Authorization authorization) =>
        Render<AuthorizationEditComponent>(parameters => parameters.Add(component => component.Subject, authorization));

    private static IReadOnlyList<string> GetRoleNames(IRenderedComponent<AuthorizationEditComponent> component) =>
        component.FindAll("[data-testid^='authorization-role-row-']").Select(row => row.GetAttribute("data-role")!).ToList();

    private static Task ClickAsync(IRenderedComponent<AuthorizationEditComponent> component, string testId) =>
        component.Find($"[data-testid='{testId}']").ClickAsync(new MouseEventArgs());

    private static Task TypeNameAsync(IRenderedComponent<AuthorizationEditComponent> component, string name) =>
        component.Find("[data-testid='authorization-role-name'] input").InputAsync(new ChangeEventArgs { Value = name });

    private static async Task AddRoleAsync(IRenderedComponent<AuthorizationEditComponent> component, string name)
    {
        await ClickAsync(component, "authorization-add-role");
        await TypeNameAsync(component, name);
        await ClickAsync(component, "authorization-role-name-confirm");
    }
}
