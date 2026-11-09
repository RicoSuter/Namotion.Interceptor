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
    /// Gets a number that is odd while the registry changes the registered subjects, their parents, children, child
    /// indices or whether they are attached (<see cref="RegisteredSubject.IsAttached"/>), and even otherwise. Each such
    /// change raises it by two, and a property added to a registered subject at run time raises it by two after the
    /// property is published. It never decreases.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A write that only adds, removes or replaces another key or position of a subject the property already holds
    /// counts as a change of child indices, although the entries, which carry the first of them, can stay as they are.
    /// </para>
    /// <para>
    /// An even number marks the end of one change, not of a whole property write: one write can make several changes,
    /// such as detaching a subtree or replacing the items of a collection, and the number is even between them.
    /// </para>
    /// <para>
    /// A reader that reads the number, reads the registry, issues a full fence such as
    /// <see cref="Interlocked.MemoryBarrier"/> and reads the number again read no such change, not even part of one,
    /// when both reads return the same even number. The fence is required for every reader, see the structure version
    /// section of the registry documentation.
    /// </para>
    /// <para>
    /// A cache of anything computed from the registry reads the number before computing and serves an entry only while
    /// it is unchanged, so an entry computed during a change carries a number from before the change ended and is never
    /// served after it.
    /// </para>
    /// </remarks>
    long StructureVersion { get; }
}
