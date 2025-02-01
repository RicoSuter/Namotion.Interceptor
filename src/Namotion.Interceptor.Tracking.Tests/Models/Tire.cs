using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models
{
    [InterceptorSubject]
    public partial class Tire
    {
        public partial decimal Pressure { get; set; }
    }
}