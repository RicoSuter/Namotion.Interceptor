using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models
{
    [InterceptorSubject]
    public partial class UntrackedReferenceLeaf
    {
        public partial int Value { get; set; }
    }

    [InterceptorSubject]
    public partial class UntrackedReferenceHolder
    {
        // A plain field: reads through it are not tracked, so moving the leaf does not recalculate Observed.
        public UntrackedReferenceLeaf? Watched;

        public partial UntrackedReferenceLeaf? First { get; set; }

        public partial int Trigger { get; set; }

        // Reads the leaf through a tracked property, so the leaf's Value has tracking data from the start.
        [Derived]
        public int Structural => First?.Value ?? 0;

        [Derived]
        public int Observed => Trigger > 0 ? (Watched?.Value ?? -1) : -1;
    }
}
