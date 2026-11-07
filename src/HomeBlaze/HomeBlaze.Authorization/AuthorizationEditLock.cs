namespace HomeBlaze.Authorization;

/// <summary>
/// Serializes the changes of the custom roles, of every user: the operations that add, rename and delete roles and the
/// saves of the role dialogs check what their change is based on and assign it while holding <see cref="Instance"/>, so
/// that no other change lands in between. Taken before <see cref="AuthorizationService.BeginUpdate"/>, and never held
/// across an await.
/// </summary>
/// <remarks>
/// <para>
/// A reload of the file replaces the roles from the storage and does not take the lock. A reload landing between the
/// check and the assignment of an operation can still be overwritten by it, and makes a save of a role dialog land in a
/// role the reload removed.
/// </para>
/// <para>
/// A write of the storage does not take the lock either. A save assigns the includes, the allow rules and the deny rules
/// one after the other, and a rename assigns <see cref="Authorization.Roles"/> twice, so that a write started by another
/// change can serialize the <see cref="Authorization"/> in between. The file holds the whole change once writes settle,
/// but a crash or a failed later write in between leaves a part of it in the file.
/// </para>
/// </remarks>
public static class AuthorizationEditLock
{
    /// <summary>Taken around the check and the assignment of a role change.</summary>
    public static readonly Lock Instance = new();
}
