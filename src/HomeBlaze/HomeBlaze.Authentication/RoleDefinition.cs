using System.Text.Json;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authentication;

/// <summary>The stored definition of a custom role, the JSON of <see cref="Data.ApplicationRole.Definition"/>.</summary>
internal sealed record RoleDefinition(string[] Includes, PermissionRule[] Allow, PermissionRule[] Deny)
{
    public static RoleDefinition From(AuthorizationRole role) => new([.. role.Includes], [.. role.Allow], [.. role.Deny]);

    public AuthorizationRole ToRole(string name) => new() { Name = name, Includes = [.. Includes], Allow = [.. Allow], Deny = [.. Deny] };

    public string Serialize() => JsonSerializer.Serialize(this);

    public static RoleDefinition Deserialize(string json)
    {
        var definition = JsonSerializer.Deserialize<RoleDefinition>(json);
        return new RoleDefinition(definition?.Includes ?? [], definition?.Allow ?? [], definition?.Deny ?? []);
    }
}
