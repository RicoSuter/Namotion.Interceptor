using Microsoft.AspNetCore.Identity;

namespace HomeBlaze.Authentication.Data;

/// <summary>A role of the authentication database.</summary>
public class ApplicationRole : IdentityRole
{
    /// <summary>The includes, allow and deny rules of a custom role as JSON (<c>RoleDefinition</c>); empty for a built-in role.</summary>
    public string Definition { get; set; } = "{}";

    /// <summary>Whether this row stands for a built-in role, whose definition is in code.</summary>
    public bool IsBuiltIn { get; set; }
}
