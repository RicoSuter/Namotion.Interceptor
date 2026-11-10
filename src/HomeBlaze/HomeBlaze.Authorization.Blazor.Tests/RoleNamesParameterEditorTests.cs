using Bunit;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Operations;
using HomeBlaze.Components.Tests.Access;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

public sealed class RoleNamesParameterEditorTests : BunitContext, IAsyncLifetime
{
    private static readonly MethodParameter RolesParameter =
        new() { Name = "roles", Type = typeof(string[]), Editor = AuthorizationParameterEditors.RoleNames };

    private readonly TestAuthorizationGraph _graph = TestAuthorizationGraph.Create("kitchen", "garden");
    private readonly TestUserPermissions _permissions = new() { HeldRoles = [BuiltInRoles.EverythingName] };
    private readonly IRenderedComponent<MudPopoverProvider> _popoverProvider;
    private readonly List<object?> _reportedValues = [];

    public RoleNamesParameterEditorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_graph.Service);
        Services.AddSingleton<IUserPermissions>(_permissions);
        Services.AddMethodParameterEditor<RoleNamesParameterEditor>(AuthorizationParameterEditors.RoleNames);
        _popoverProvider = Render<MudPopoverProvider>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _graph.Dispose();
    }

    [Fact]
    public async Task WhenAFullAdministratorAssignsRoles_ThenEveryUsableRoleIsOffered()
    {
        // Arrange
        var editor = RenderEditor(null);

        // Act
        var options = await GetOfferedNamesAsync(editor);

        // Assert
        Assert.Equal([BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, BuiltInRoles.EverythingName, "garden", "kitchen"], options);
    }

    [Fact]
    public async Task WhenAUserManagerAssignsRoles_ThenOnlyTheRolesTheyHoldAreOffered()
    {
        // Arrange
        _permissions.HeldRoles.Clear();
        _permissions.HeldRoles.UnionWith([BuiltInRoles.ReaderName, "kitchen"]);
        var editor = RenderEditor(null);

        // Act
        var options = await GetOfferedNamesAsync(editor);

        // Assert
        Assert.Equal([BuiltInRoles.ReaderName, "kitchen"], options);
    }

    [Fact]
    public async Task WhenTheRolesOfThePersonChange_ThenTheOfferedRolesFollow()
    {
        // Arrange
        _permissions.HeldRoles.Clear();
        _permissions.HeldRoles.Add(BuiltInRoles.ReaderName);
        var editor = RenderEditor(null);

        // Act
        _permissions.HeldRoles.Add("garden");
        await editor.InvokeAsync(_permissions.RaiseChanged);
        var options = await GetOfferedNamesAsync(editor);

        // Assert
        Assert.Equal([BuiltInRoles.ReaderName, "garden"], options);
    }

    [Fact]
    public void WhenTheEditorStartsWithoutAValue_ThenItReportsNoRoles()
    {
        // Act
        RenderEditor(null);

        // Assert
        var reported = Assert.Single(_reportedValues);
        Assert.Empty(Assert.IsType<string[]>(reported));
    }

    [Fact]
    public void WhenTheEditorStartsWithAValue_ThenItShowsItsRolesAndReportsNothing()
    {
        // Act
        var editor = RenderEditor(new[] { "kitchen", BuiltInRoles.ReaderName });

        // Assert
        Assert.Equal(["kitchen", BuiltInRoles.ReaderName], editor.FindAll("[data-testid^='parameter-role-']").Select(chip => chip.TextContent.Trim()));
        Assert.Empty(_reportedValues);
    }

    [Fact]
    public async Task WhenSetRolesOfAUserIsInvoked_ThenThePickerStartsWithTheRolesOfTheUser()
    {
        // Arrange
        _graph.Authorization.ReplaceUsers(
        [
            new UserSnapshot("mom", null, ["kitchen", BuiltInRoles.ReaderName], IsLockedOut: false, MustChangePassword: false, LastLogin: null, IsBuiltIn: false, HasKey: false)
        ]);
        var setRoles = _graph.Authorization.Users["mom"].TryGetRegisteredSubject()!.TryGetProperty("SetRoles")!;
        var dialogProvider = Render<MudDialogProvider>();

        // Act: the invocation waits for the dialog, which the test leaves open.
        await dialogProvider.InvokeAsync(() =>
        {
            _ = OperationInvoker.InvokeAsync(Services.GetRequiredService<IDialogService>(), Services, _permissions, setRoles, CancellationToken.None);
        });

        // Assert
        await dialogProvider.WaitForAssertionAsync(() => Assert.Single(dialogProvider.FindComponents<RoleNamesParameterEditor>()), TimeSpan.FromSeconds(30));
        var picker = dialogProvider.FindComponent<RoleNamesParameterEditor>();
        Assert.Equal(["kitchen", BuiltInRoles.ReaderName], picker.FindAll("[data-testid^='parameter-role-']").Select(chip => chip.TextContent.Trim()));
    }

    [Fact]
    public async Task WhenRolesAreSelected_ThenTheEditorReportsThemAsAnArray()
    {
        // Arrange
        var editor = RenderEditor(null);
        var select = editor.FindComponent<MudSelect<string>>();

        // Act
        await editor.InvokeAsync(() => select.Instance.SelectedValuesChanged.InvokeAsync([BuiltInRoles.ReaderName, "kitchen"]));

        // Assert
        Assert.Equal([BuiltInRoles.ReaderName, "kitchen"], Assert.IsType<string[]>(_reportedValues[^1]));
    }

    [Fact]
    public void WhenTheSelectedRolesLackARoleWithDenyRulesOfThePerson_ThenTheEditorNamesIt()
    {
        // Arrange
        _permissions.RolesWithDenyRules.Add("garden");

        // Act
        var editor = RenderEditor(new[] { BuiltInRoles.ReaderName });

        // Assert
        Assert.Contains("must include your roles with deny rules: garden", editor.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheSelectedRolesIncludeTheRolesWithDenyRulesOfThePerson_ThenTheEditorNamesNone()
    {
        // Arrange
        _permissions.RolesWithDenyRules.Add("garden");

        // Act
        var editor = RenderEditor(new[] { "garden" });

        // Assert
        Assert.DoesNotContain("deny rules", editor.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheDialogReportsAnError_ThenTheEditorShowsIt()
    {
        // Act
        var editor = Render<RoleNamesParameterEditor>(parameters => parameters
            .Add(component => component.Parameter, RolesParameter)
            .Add(component => component.Label, "Roles *")
            .Add(component => component.Value, Array.Empty<string>())
            .Add(component => component.ErrorText, "This field is required"));

        // Assert
        Assert.Contains("Roles *", editor.Markup, StringComparison.Ordinal);
        Assert.Contains("This field is required", editor.Markup, StringComparison.Ordinal);
    }

    private IRenderedComponent<RoleNamesParameterEditor> RenderEditor(object? value) =>
        Render<RoleNamesParameterEditor>(parameters => parameters
            .Add(component => component.Parameter, RolesParameter)
            .Add(component => component.Label, "Roles *")
            .Add(component => component.Value, value)
            .Add(component => component.ValueChanged, (object? reported) => _reportedValues.Add(reported)));

    private async Task<IReadOnlyList<string?>> GetOfferedNamesAsync(IRenderedComponent<RoleNamesParameterEditor> editor)
    {
        var select = editor.FindComponent<MudSelect<string>>();
        await editor.InvokeAsync(select.Instance.OpenMenu);
        return _popoverProvider.FindComponents<MudSelectItem<string>>().Select(item => item.Instance.Value).ToList();
    }
}
