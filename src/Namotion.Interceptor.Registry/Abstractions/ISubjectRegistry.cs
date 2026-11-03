namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// A registry which tracks subjects and their child subjects, property attributes and additional metadata.
/// </summary>
public interface ISubjectRegistry
{
    /// <summary>
    /// Gets all known registered subjects.
    /// </summary>
    IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects { get; }
    
    /// <summary>
    /// Gets a registered subject by the subject instance.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The registered subject or null if it is not registered with the registry.</returns>
    RegisteredSubject? TryGetRegisteredSubject(IInterceptorSubject subject);

    /// <summary>
    /// Gets a number that increases after every change to the registered subjects, their parents, children and child
    /// indices, and after a property is added to a registered subject at run time. It never decreases.
    /// </summary>
    /// <remarks>
    /// A cache of anything computed from the registry reads it before computing and serves an entry only while it is
    /// unchanged, so an entry computed during a change carries the number from before it.
    /// </remarks>
    long StructureVersion { get; }
}
