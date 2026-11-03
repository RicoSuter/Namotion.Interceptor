using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Metadata;

/// <summary>
/// Gives the subjects below it methods of its own, such as a storage that offers to delete the subjects stored in its
/// files.
/// </summary>
public interface IChildMethodProvider
{
    /// <summary>
    /// Adds the operations and queries that the provider offers for the subject to its registered subject, as
    /// <see cref="MethodMetadata"/> properties. Called when the subject attaches or gets its first parent, with this
    /// provider the nearest one along the first-parent chain of the subject, after the subject's own methods are
    /// registered. Synchronous and thread-safe.
    /// </summary>
    /// <remarks>
    /// Runs inside the lifecycle lock of the context while the subject attaches, so it must not block, wait on another
    /// thread or write properties that hold subjects. Only the subject that attached or got its first parent is passed,
    /// not the subjects below it. It is called again each time the subject attaches again or gets a first parent again,
    /// and the properties it added before are still there, so it must check for them before it adds them.
    /// </remarks>
    /// <param name="subject">The subject that attached below the provider.</param>
    void AddMethods(IInterceptorSubject subject);
}
