using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Blazor;

/// <summary>Whether the viewer may change what the editors of a subject or a file change.</summary>
public static class EditPermissions
{
    /// <summary>
    /// Gets whether the viewer may change the content of a file: administer <see cref="MarkdownFile.Content"/> of a
    /// Markdown file, and the member of the folder that holds any other file, which has no member for its content. False
    /// for a subject that is no file.
    /// </summary>
    public static bool CanAdministerFileContent(this IUserPermissions permissions, IInterceptorSubject file) =>
        file switch
        {
            MarkdownFile markdownFile => TryGetContentMember(markdownFile) is { } member && permissions.CanAdminister(member),
            IStorageFile => permissions.CanAdministerHoldingMembers(file),
            _ => false
        };

    /// <summary>Gets the member that holds the content of a Markdown file, which <see cref="CanAdministerFileContent"/> checks.</summary>
    internal static RegisteredSubjectProperty? TryGetContentMember(MarkdownFile file) =>
        file.TryGetRegisteredSubject()?.TryGetProperty(nameof(MarkdownFile.Content));

    /// <summary>
    /// Gets whether the viewer may use the editor of the subject: change the content of a file, see
    /// <see cref="CanAdministerFileContent"/>; for a subject with an edit component and without configuration members,
    /// save it through the edit operation of the subject, see
    /// <see cref="RegisteredSubjectMethodExtensions.CanSaveThroughEditOperation"/>; or administer the configuration of any
    /// other subject, see <see cref="UserPermissionsExtensions.CanAdministerConfiguration"/>, which requires at least one
    /// configuration member.
    /// </summary>
    /// <param name="permissions">The permissions of the viewer.</param>
    /// <param name="subject">The subject to edit.</param>
    /// <param name="hasEditComponent">Whether the subject has an edit component, which alone saves through its edit operation.</param>
    public static bool CanEdit(this IUserPermissions permissions, IInterceptorSubject subject, bool hasEditComponent)
    {
        if (subject is IStorageFile)
        {
            return permissions.CanAdministerFileContent(subject);
        }

        return (hasEditComponent && permissions.CanSaveThroughEditOperation(subject)) || permissions.CanAdministerConfiguration(subject);
    }
}
