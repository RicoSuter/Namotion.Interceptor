using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Services;

/// <summary>
/// Reads the metadata subject that a storage gives a subject stored as a JSON file, a folder and its root, in the
/// dynamic registry property <see cref="IMetadataSubject.PropertyName"/>.
/// </summary>
public static class SubjectMetadataExtensions
{
    /// <summary>
    /// Gets the metadata subject of the subject, null when it has none or is not registered.
    /// </summary>
    public static IMetadataSubject? TryGetMetadataSubject(this IInterceptorSubject subject)
        => subject.TryGetRegisteredSubject()?.TryGetProperty(IMetadataSubject.PropertyName)?.GetValue() as IMetadataSubject;

    /// <summary>
    /// Gets the navigation metadata of the subject, null when it has none or is not registered.
    /// </summary>
    public static SubjectMetadata? TryGetSubjectMetadata(this IInterceptorSubject subject)
        => subject.TryGetMetadataSubject()?.SubjectMetadata;
}
