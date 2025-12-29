namespace HomeBlaze.Authorization.Services;

/// <summary>
/// Service for expanding roles according to the role hierarchy.
/// A role includes all roles it inherits from (e.g., Admin includes Supervisor, Operator, User, Guest, Anonymous).
/// </summary>
public interface IRoleExpander
{
    /// <summary>
    /// Gets whether the role expander has been initialized.
    /// </summary>
    bool IsInitialized { get; } // TODO: Unused, remove

    /// <summary>
    /// Initializes the role expander by loading the role hierarchy from the database.
    /// Must be called during application startup before any role expansion is performed.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Expands the given roles to include all inherited roles.
    /// </summary>
    /// <param name="roles">The roles to expand.</param>
    /// <returns>A set containing all input roles plus their inherited roles.</returns>
    IReadOnlySet<string> ExpandRoles(IEnumerable<string> roles);

    /// <summary>
    /// Reloads the role composition from the database.
    /// Call this when role hierarchy changes are made via the admin UI.
    /// </summary>
    Task ReloadAsync();
}
