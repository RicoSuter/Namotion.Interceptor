using Microsoft.AspNetCore.Identity;

namespace HomeBlaze.Authorization.Data;

/// <summary>
/// Extended Identity user with additional properties for HomeBlaze.
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>
    /// User's display name (shown in UI).
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// When the user account was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the user last logged in.
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Whether the user must change their password on next login.
    /// </summary>
    public bool MustChangePassword { get; set; }
}
