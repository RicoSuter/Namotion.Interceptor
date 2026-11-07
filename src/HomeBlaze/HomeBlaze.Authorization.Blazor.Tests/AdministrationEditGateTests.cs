using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

/// <summary>
/// The editor of a role is offered only to a viewer who may administer the roles, which it changes, and so is the preview
/// of a role, which shows the members of any subject.
/// </summary>
public sealed class AdministrationEditGateTests : BunitContext, IAsyncLifetime
{
    private readonly TestAuthorizationGraph _graph = TestAuthorizationGraph.Create("kitchen");
    private readonly TestUserPermissions _permissions = new();

    public AdministrationEditGateTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_graph.Service);
        Services.AddSingleton<IRoleStore>(_graph.RoleStore);
        Services.AddSingleton<IUserPermissions>(_permissions);
        Services.AddSingleton<IUserAdministration>(new FakeUserAdministration(_graph.Authorization));
        Services.AddSingleton(_graph.PathResolver);

        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(Role), typeof(TestRoot), typeof(TestDevice)]);
        Services.AddSingleton(new SubjectTypeCatalog(new SubjectTypeRegistry(typeProvider)));

        _graph.Authorization.ReplaceUsers(
        [
            new UserSnapshot("alice", null, [BuiltInRoles.EverythingName], false, false, null, false, false),
            new UserSnapshot("kid", null, [BuiltInRoles.ReaderName], false, false, null, false, false)
        ]);

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
    public void WhenTheViewerMayNotAdministerTheRoles_ThenARoleOffersNoEditor()
    {
        // Arrange
        var role = (ISubjectEditPermission)_graph.GetRole("kitchen");
        var canEditBefore = role.CanEdit(_permissions);

        // Act
        _permissions.CannotAdminister.Add(GetMember(_graph.Authorization, nameof(Authorization.Roles)));

        // Assert
        Assert.True(canEditBefore);
        Assert.False(role.CanEdit(_permissions));
    }

    [Fact]
    public async Task WhenTheViewerMayNotAdministerTheRoles_ThenThePreviewOfARoleIsNotRendered()
    {
        // Arrange
        _permissions.CannotAdminister.Add(GetMember(_graph.Authorization, nameof(Authorization.Roles)));

        // Act
        var component = Render<RoleEditComponent>(parameters => parameters.Add(editor => editor.Subject, _graph.GetRole("kitchen")));
        var tabs = component.FindComponent<MudTabs>();
        await component.InvokeAsync(() => tabs.Instance.ActivatePanelAsync(3));

        // Assert
        Assert.Empty(component.FindAll("[data-testid='role-preview-path']"));
    }

    [Fact]
    public async Task WhenTheViewerMayNotAdministerTheRoles_ThenAPathChosenInThePreviewShowsNothing()
    {
        // Arrange
        _permissions.CannotAdminister.Add(GetMember(_graph.Authorization, nameof(Authorization.Roles)));
        var preview = Render<RolePreview>(parameters => parameters
            .Add(component => component.Role, new AuthorizationRole { Name = "kitchen", Includes = [BuiltInRoles.ReaderName] })
            .Add(component => component.Authorization, _graph.Authorization));

        // Act
        await preview.Find("[data-testid='role-preview-path'] input").InputAsync(new ChangeEventArgs { Value = "/Devices" });

        // Assert
        Assert.Empty(preview.FindAll("[data-testid='role-preview-readable']"));
        Assert.Empty(preview.FindAll("[data-testid='role-preview-matrix']"));
    }

    private static RegisteredSubjectProperty GetMember(IInterceptorSubject subject, string name) =>
        subject.TryGetRegisteredSubject()!.TryGetProperty(name)!;
}
