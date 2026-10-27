namespace HomeBlaze.Authorization.Blazor;

/// <summary>
/// Serializes the saves of the Authorization dialog and of the role dialogs, of every user: a save checks that what it
/// loaded is unchanged and assigns its changes while holding <see cref="Instance"/>, so that no other save lands in
/// between. Taken before <see cref="AuthorizationService.BeginUpdate"/>, and never held across an await.
/// </summary>
/// <remarks>
/// A reload of the file replaces the roles from the storage and does not take the lock. A reload landing between the
/// check and the assignment of a save can still be overwritten by a save of the Authorization dialog, and makes a save
/// of a role dialog land in a role the reload removed.
/// </remarks>
internal static class AuthorizationEditLock
{
    public static readonly Lock Instance = new();
}
