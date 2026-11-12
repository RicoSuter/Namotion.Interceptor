using HomeBlaze.Authorization.Abstractions;
using Microsoft.AspNetCore.Components;

namespace HomeBlaze.Components.Abstractions;

/// <summary>
/// A component that answers from the <see cref="IUserPermissions"/> of the viewer and renders again when
/// <see cref="IUserPermissions.Changed"/> is raised. A component that overrides <see cref="OnInitialized"/> or
/// <see cref="Dispose(bool)"/> calls the base.
/// </summary>
public abstract class PermissionsComponentBase : ComponentBase, IDisposable
{
    /// <summary>Gets the permissions of the viewer.</summary>
    [Inject]
    protected IUserPermissions Permissions { get; set; } = null!;

    protected override void OnInitialized()
    {
        Permissions.Changed += OnPermissionsChanged;
    }

    private void OnPermissionsChanged() => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops rendering again on <see cref="IUserPermissions.Changed"/>. A component that overrides it calls the base.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Permissions.Changed -= OnPermissionsChanged;
        }
    }
}
