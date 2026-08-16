namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Reconciles the ordered child relationships of a parent property.
/// </summary>
public interface IPropertyRelationshipHandler
{
    /// <summary>
    /// Reconciles the complete ordered sequence of child relationships held by a property.
    /// </summary>
    /// <param name="property">The parent property.</param>
    /// <param name="relationships">The complete ordered sequence of child relationships.</param>
    void ReconcileChildRelationships(
        PropertyReference property,
        ReadOnlySpan<SubjectPropertyRelationship> relationships);
}
