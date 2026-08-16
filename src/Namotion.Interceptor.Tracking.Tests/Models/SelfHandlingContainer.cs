using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Models
{
    /// <summary>
    /// Handles its own property lifecycle, which the interceptor dispatches to besides the context services.
    /// </summary>
    [InterceptorSubject]
    public partial class SelfHandlingContainer : IPropertyLifecycleHandler, IPropertyRelationshipHandler
    {
        public SelfHandlingContainer()
        {
            Items = [];
        }

        public partial Person[] Items { get; set; }

        /// <summary>
        /// The collection values supplied to each structural refresh.
        /// </summary>
        public List<object?> Refreshes { get; } = [];

        /// <summary>
        /// The relationships of each reconciliation, copied out because the span does not outlive the call.
        /// </summary>
        public List<SubjectPropertyRelationship[]> RelationshipReconciliations { get; } = [];

        public List<string>? RelationshipHandlerCallOrder { get; set; }

        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change)
        {
        }

        public void RefreshCollectionProperty(PropertyReference property, object? value)
        {
            Refreshes.Add(value);
        }

        public void ReconcileChildRelationships(PropertyReference property, ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            RelationshipHandlerCallOrder?.Add("subject");
            RelationshipReconciliations.Add(relationships.ToArray());
        }
    }
}
