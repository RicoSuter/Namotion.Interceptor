using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization.Blazor.Tests;

/// <summary>
/// A role store that records every change and returns <see cref="Result"/>, with the new stamp when it succeeded; a
/// successful change is applied to the roles of the authorization with <see cref="Authorization.ReplaceRoles"/>,
/// keeping their order.
/// </summary>
internal sealed class FakeRoleStore(Authorization? target = null) : IRoleStore
{
    /// <summary>The stamp of every role.</summary>
    public string? Stamp { get; set; } = "stamp";

    /// <summary>The stamp of every role after a successful change, or null to keep <see cref="Stamp"/>.</summary>
    public string? StampAfterChange { get; set; }

    /// <summary>The result of every change.</summary>
    public RoleChangeResult Result { get; set; } = new(true, null);

    public List<(string? OriginalName, AuthorizationRole Role, string? Stamp)> Saves { get; } = [];

    public List<(string Name, string? Stamp)> Deletes { get; } = [];

    public List<(string OldName, string NewName)> Renames { get; } = [];

    public string? GetConcurrencyStamp(string name) => Stamp;

    public Task<RoleChangeResult> SaveRoleAsync(string? originalName, AuthorizationRole role, string? concurrencyStamp, CancellationToken cancellationToken)
    {
        Saves.Add((originalName, role, concurrencyStamp));
        if (!Result.Succeeded)
        {
            return Task.FromResult(Result);
        }

        Apply(originalName, role);
        Stamp = StampAfterChange ?? Stamp;
        return Task.FromResult(Result with { ConcurrencyStamp = Stamp });
    }

    /// <summary>Renames the role keeping its rules as held now, and renames it in the includes of the other roles, as the store does.</summary>
    public Task<RoleChangeResult> RenameRoleAsync(string oldName, string newName, CancellationToken cancellationToken)
    {
        Renames.Add((oldName, newName));
        if (!Result.Succeeded || target is null)
        {
            return Task.FromResult(Result);
        }

        var roles = GetRoles()
            .Select(role => role with
            {
                Name = string.Equals(role.Name, oldName, StringComparison.OrdinalIgnoreCase) ? newName : role.Name,
                Includes = [.. role.Includes.Select(include => string.Equals(include, oldName, StringComparison.OrdinalIgnoreCase) ? newName : include)]
            })
            .ToList();
        target.ReplaceRoles(roles);
        Stamp = StampAfterChange ?? Stamp;
        return Task.FromResult(Result with { ConcurrencyStamp = Stamp });
    }

    public Task<RoleChangeResult> DeleteRoleAsync(string name, string? concurrencyStamp, CancellationToken cancellationToken)
    {
        Deletes.Add((name, concurrencyStamp));
        if (Result.Succeeded && target is not null)
        {
            target.ReplaceRoles([.. GetRoles().Where(role => !string.Equals(role.Name, name, StringComparison.OrdinalIgnoreCase))]);
            Stamp = StampAfterChange ?? Stamp;
        }

        return Task.FromResult(Result);
    }

    /// <summary>Replaces the role named <paramref name="originalName"/> with <paramref name="role"/>, or adds it when the name is null.</summary>
    public void Apply(string? originalName, AuthorizationRole role)
    {
        if (target is null)
        {
            return;
        }

        var roles = GetRoles();
        var index = originalName is null ? -1 : roles.FindIndex(existing => string.Equals(existing.Name, originalName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            roles[index] = role;
        }
        else
        {
            roles.Add(role);
        }

        target.ReplaceRoles(roles);
    }

    private List<AuthorizationRole> GetRoles() =>
        target!.Roles
            .Select(entry => new AuthorizationRole { Name = entry.Key, Includes = [.. entry.Value.Includes], Allow = [.. entry.Value.Allow], Deny = [.. entry.Value.Deny] })
            .ToList();
}
