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
    /// Subjects the update creates are populated before they enter the graph, against an empty interceptor
    /// chain: their initial values run no validation, equality check or derived-property recalculation, raise
    /// no change events, and skip <paramref name="transformValueBeforeApply"/>. Values written to subjects
    /// that already exist locally take the normal intercepted path.
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
    /// <param name="logger">Reports the warnings of this apply, such as unresolvable-subject drops with the
    /// origin; omit to use the subject context's <see cref="ILoggerFactory"/>, and without one the drops are
    /// counter-only.</param>
    /// <returns>
    /// <c>true</c> if every part of the update that referenced a resolvable subject, collection item or
    /// dictionary entry applied; <c>false</c> if one of those could not be resolved and was dropped, see
    /// <see cref="SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates"/>. An inbound property update
    /// for a property the receiving subject does not declare is dropped too, but is a permanent schema
    /// mismatch rather than a transient resolution failure, so it is excluded from this flag rather than
    /// stalling a connection that could never recover from it; it is counted separately, see
    /// <see cref="SubjectUpdateDiagnostics.UnknownInboundProperties"/>. Structure that a property without a
    /// setter cannot store is excluded for the same reason and reported as a warning. A caller that treats
    /// its own apply as an acknowledgement, such as a server advancing what it has applied for a connection,
    /// must not do so when this returns <c>false</c>.
    /// </returns>
    public static bool ApplySubjectUpdate(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectFactory? subjectFactory,
        ChangeOrigin origin,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null,
        ILogger? logger = null)
    {
        return SubjectUpdateApplier.ApplyUpdate(
            subject,
            update,
            subjectFactory ?? DefaultSubjectFactory.Instance,
            origin,
            transformValueBeforeApply,
            logger);
    }
}
