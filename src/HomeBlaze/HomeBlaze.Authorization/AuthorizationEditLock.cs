namespace HomeBlaze.Authorization;

/// <summary>
/// Serializes the changes of the custom roles, of every user: the operations that add, rename and delete roles and the
/// saves of the role dialogs check what their change is based on and assign it while holding <see cref="Instance"/>, so
/// that no other change lands in between. Taken before <see cref="AuthorizationService.BeginUpdate"/>, and never held
/// across an await.
/// </summary>
/// <remarks>
/// A reload of the file replaces the roles from the storage and does not take the lock. A reload landing between the
/// check and the assignment of an operation can still be overwritten by it, and makes a save of a role dialog land in a
/// role the reload removed.
/// </remarks>
public static class AuthorizationEditLock
{
    /// <summary>Taken around the check and the assignment of a role change.</summary>
    public static readonly Lock Instance = new();
}
