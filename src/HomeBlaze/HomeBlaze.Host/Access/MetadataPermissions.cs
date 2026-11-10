using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Storage;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Host.Access;

/// <summary>
/// Whether the viewer may change metadata, asked with <see cref="IUserPermissions.CanWrite"/> for the member that holds it,
/// as agents are: metadata is no configuration; and the metadata the viewer may read.
/// </summary>
internal static class MetadataPermissions
{
    /// <summary>
    /// Gets whether the viewer may write the metadata of the subject, or of its property <paramref name="propertyName"/>;
    /// false when there is no member for it. Adds the member when it is missing, see
    /// <see cref="FluentStorageContainer.GetOrAddMetadataMember"/>.
    /// </summary>
    public static bool CanWriteMetadata(this IUserPermissions permissions, IInterceptorSubject subject, string? propertyName) =>
        subject.TryGetRegisteredSubject() is { } registered &&
        FluentStorageContainer.GetOrAddMetadataMember(registered, propertyName) is { } member &&
        permissions.CanWrite(member);

    /// <summary>
    /// Gets the metadata of the subject when the viewer may read the member that holds it; null without any or otherwise.
    /// </summary>
    public static ImmutableDictionary<string, JsonElement>? GetReadableMetadata(this IUserPermissions permissions, IInterceptorSubject subject) =>
        subject.TryGetRegisteredSubject()?.TryGetProperty(KnownAttributes.Metadata) is { } member && permissions.CanRead(member)
            ? member.GetValue() as ImmutableDictionary<string, JsonElement>
            : null;

    /// <summary>
    /// Gets the metadata of the property when the viewer may read the attribute that holds it; null without any or otherwise.
    /// </summary>
    public static ImmutableDictionary<string, JsonElement>? GetReadableMetadata(this IUserPermissions permissions, RegisteredSubjectProperty property) =>
        property.TryGetAttribute(KnownAttributes.Metadata) is { } member && permissions.CanRead(member)
            ? member.GetValue() as ImmutableDictionary<string, JsonElement>
            : null;

    /// <summary>Gets whether the viewer may set the order of the children of a folder, which its own metadata holds.</summary>
    public static bool CanReorder(this IUserPermissions permissions, IInterceptorSubject folder) =>
        permissions.CanWriteMetadata(folder, null);
}
