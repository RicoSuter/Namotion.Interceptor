using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Models
{
    /// <summary>
    /// Handles its own property lifecycle, which the interceptor dispatches to besides the context services.
    /// </summary>
    [InterceptorSubject]
    public partial class SelfHandlingContainer : IPropertyLifecycleHandler
    {
        public SelfHandlingContainer()
        {
            Items = [];
        }

        public partial Person[] Items { get; set; }

        /// <summary>
        /// The children of each refresh, copied out because the span does not outlive the call.
        /// </summary>
        public List<SubjectChildReference[]> Refreshes { get; } = [];

        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change)
        {
        }

        public void RefreshChildIndices(PropertyReference property, ReadOnlySpan<SubjectChildReference> children)
        {
            Refreshes.Add(children.ToArray());
        }
    }
}
