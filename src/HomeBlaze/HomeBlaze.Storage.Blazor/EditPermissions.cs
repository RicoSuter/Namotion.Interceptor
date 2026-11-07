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
    /// Gets whether the viewer may use the editor of the subject: change the content of a file, what an
    /// <see cref="ISubjectEditPermission"/> decides, or administer some configuration member of any other subject. A
    /// subject without configuration members can be edited by its edit component alone, which checks the members it
    /// changes itself.
    /// </summary>
    public static bool CanEdit(this IUserPermissions permissions, IInterceptorSubject subject)
    {
        if (subject is IStorageFile)
        {
            return permissions.CanAdministerFileContent(subject);
        }

        if (subject is ISubjectEditPermission editPermission)
        {
            return editPermission.CanEdit(permissions);
        }

        var hasConfiguration = false;
        foreach (var member in subject.GetConfigurationProperties())
        {
            if (permissions.CanAdminister(member))
            {
                return true;
            }

            hasConfiguration = true;
        }

        return !hasConfiguration;
    }
}
