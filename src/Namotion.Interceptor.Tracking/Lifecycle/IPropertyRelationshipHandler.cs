namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Reconciles the ordered child relationships of a parent property.
/// </summary>
/// <remarks>
/// Handlers are invoked synchronously in resolver order while the calling lifecycle interceptor holds
/// its structural lock. Dispatch continues after a handler failure and rethrows the first exception after
/// all handlers have run. Handlers must therefore be fast, non-blocking, and thread-safe across lifecycle
/// authorities. During ordinary reconciliation, a handler may write a different property. Writing the
/// property currently being reconciled, or any structural property on the same subject during its initial
/// attach, throws <see cref="InvalidOperationException"/> before terminal backing mutation.
/// </remarks>
public interface IPropertyRelationshipHandler
{
    /// <summary>
    /// Reconciles the complete source-ordered sequence of child relationships held by a property.
    /// An empty sequence clears the property relationship group.
    /// </summary>
    /// <param name="property">The parent property.</param>
    /// <param name="relationships">The complete ordered sequence of child relationships.</param>
    void ReconcileChildRelationships(
        PropertyReference property,
        ReadOnlySpan<SubjectPropertyRelationship> relationships);
}
