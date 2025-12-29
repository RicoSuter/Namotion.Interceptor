using MudBlazor;

namespace HomeBlaze.Authorization.Blazor.Utilities;

/// <summary>
/// Utility class for consistent role display styling across the authorization UI.
/// </summary>
public static class RoleDisplayUtility
{
    /// <summary>
    /// Gets a MudBlazor Color for a role based on its privilege level.
    /// </summary>
    /// <param name="role">The role name.</param>
    /// <returns>A MudBlazor Color representing the role's importance level.</returns>
    public static Color GetRoleColor(string role)
    {
        return role switch
        {
            "Admin" => Color.Error,
            "Supervisor" => Color.Warning,
            "Operator" => Color.Info,
            "User" => Color.Primary,
            "Guest" => Color.Default,
            "Anonymous" => Color.Surface,
            _ => Color.Secondary
        };
    }

    /// <summary>
    /// Gets a MudBlazor Icon for a role.
    /// </summary>
    /// <param name="role">The role name.</param>
    /// <returns>A MudBlazor icon string representing the role.</returns>
    public static string GetRoleIcon(string role)
    {
        return role switch
        {
            "Admin" => Icons.Material.Filled.AdminPanelSettings,
            "Supervisor" => Icons.Material.Filled.SupervisorAccount,
            "Operator" => Icons.Material.Filled.Engineering,
            "User" => Icons.Material.Filled.Person,
            "Guest" => Icons.Material.Filled.PersonOutline,
            "Anonymous" => Icons.Material.Filled.QuestionMark,
            _ => Icons.Material.Filled.Badge
        };
    }
}
