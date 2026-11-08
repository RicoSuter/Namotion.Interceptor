using System.ComponentModel.DataAnnotations;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Identity;

namespace HomeBlaze.Authentication.Data;

/// <summary>A user of the authentication database.</summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>The end of the lockout that an administrator sets, which lasts until they unlock the user.</summary>
    public static readonly DateTimeOffset AdministratorLockoutEnd = DateTimeOffset.MaxValue;

    /// <summary>The name shown for the user in place of their user name, or null, see <see cref="NameRules.GetDisplayNameError"/>.</summary>
    [MaxLength(NameRules.MaximumDisplayNameLength)]
    public string? DisplayName { get; set; }

    /// <summary>Whether the user may do nothing but set a password until they set one.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// Whether the user may sign in with an empty password: only the seeded administrator, until a password is set.
    /// </summary>
    public bool AllowsEmptyPassword { get; set; }

    /// <summary>Whether this is a built-in user (<c>anonymous</c>, <c>mcp</c>), which cannot sign in, be removed, locked or reset.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>The time of the last successful sign-in.</summary>
    public DateTimeOffset? LastLogin { get; set; }

    /// <summary>
    /// Whether a session of the user may do nothing but set a password: the user must change it, or signs in with an empty
    /// one, which confines the session even where <see cref="MustChangePassword"/> is false, such as in a database edited by hand.
    /// </summary>
    public bool RequiresPasswordChange() => MustChangePassword || AllowsEmptyPassword;

    /// <summary>Whether an administrator locked the user, unlike a lockout after failed sign-ins, which ends by itself.</summary>
    public bool IsLockedByAdministrator() => IsAdministratorLockoutEnd(LockoutEnd);

    /// <summary>Whether <paramref name="lockoutEnd"/> is <see cref="AdministratorLockoutEnd"/>.</summary>
    internal static bool IsAdministratorLockoutEnd(DateTimeOffset? lockoutEnd) => lockoutEnd == AdministratorLockoutEnd;
}
