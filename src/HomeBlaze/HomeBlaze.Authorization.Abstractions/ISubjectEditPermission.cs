namespace HomeBlaze.Authorization.Abstractions;

/// <summary>
/// A subject whose editor changes other members than its configuration members, and which therefore decides itself
/// whether a viewer may open that editor. The editor still checks each change it makes.
/// </summary>
public interface ISubjectEditPermission
{
    /// <summary>Gets whether the viewer of <paramref name="permissions"/> may open the editor of this subject.</summary>
    bool CanEdit(IUserPermissions permissions);
}
