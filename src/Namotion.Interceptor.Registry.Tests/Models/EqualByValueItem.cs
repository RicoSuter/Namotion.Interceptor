using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Models
{
    /// <summary>
    /// Declares every instance equal to every other. Child placement has to key on identity, so a lookup
    /// built with the default comparer would collapse distinct children into one entry and drop the rest.
    /// </summary>
    [InterceptorSubject]
    public partial class EqualByValueItem
    {
        public partial string? Tag { get; set; }

        public override bool Equals(object? obj) => obj is EqualByValueItem;

        public override int GetHashCode() => 0;
    }
}
