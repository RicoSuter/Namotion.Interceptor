using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Extension methods for applying SubjectUpdate to subjects.
/// </summary>
public static class SubjectUpdateExtensions
{
    /// <summary>
    /// Applies update to a subject.
    /// </summary>
    /// <remarks>
    /// Subjects the update creates are populated before they enter the graph, so that the subgraph is
    /// complete by the time a concurrent reader can observe it. A subject only inherits the graph's
    /// context once it is assigned, so those initial writes run against an empty interceptor chain:
    /// they perform no validation, no equality check, no derived-property recalculation, and raise no
    /// change events, and <paramref name="transformValueBeforeApply"/> does not run for them either
    /// because its registered property cannot be resolved yet. Values written to subjects that already
    /// exist locally take the normal intercepted path. Lifecycle correctness is unaffected: attaching
    /// the subject seeds change tracking from the backing store, so the first later write to one of
    /// these properties is compared against the applied value, not against the type default.
    /// </remarks>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="subjectFactory">The subject factory used to create subjects the update introduces,
    /// or null to use <see cref="DefaultSubjectFactory.Instance"/>.</param>
    /// <param name="origin">The origin to stamp on the applied changes. Pass <see cref="ChangeOrigin.Local"/>
    /// for local writes, or <see cref="ChangeOrigin.FromSource"/> when applying an inbound update from a
    /// source so echo suppression skips that source's own outbound path.</param>
    /// <param name="transformValueBeforeApply">The function to transform the update before applying it.
    /// Not invoked for subjects this update creates, see the remarks.</param>
    /// <param name="logger">Logs unresolvable-subject drops with the origin; omit to keep the drops
    /// counter-only.</param>
    public static void ApplySubjectUpdate(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectFactory? subjectFactory,
        ChangeOrigin origin,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null,
        ILogger? logger = null)
    {
        SubjectUpdateApplier.ApplyUpdate(
            subject,
            update,
            subjectFactory ?? DefaultSubjectFactory.Instance,
            origin,
            transformValueBeforeApply,
            logger);
    }
}
