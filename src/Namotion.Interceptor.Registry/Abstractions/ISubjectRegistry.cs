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
    /// Gets a number that is odd while the registry changes the registered subjects, their parents, children or child
    /// indices, and even otherwise. Each such change raises it by two, and a property added to a registered subject at
    /// run time raises it by two after the property is published. It never decreases.
    /// </summary>
    /// <remarks>
    /// A reader that reads the same even number before and after reading the registry read no such change; it needs a
    /// full fence, such as <see cref="Interlocked.MemoryBarrier"/>, before reading the number again. A cache of anything
    /// computed from the registry reads it before computing and serves an entry only while it is unchanged, so an entry
    /// computed during a change carries a number from before the change ended.
    /// </remarks>
    long StructureVersion { get; }
}
