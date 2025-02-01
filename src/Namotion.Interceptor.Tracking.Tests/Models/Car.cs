using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Change.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models
{
    [InterceptorSubject]
    public partial class Car
    {
        public partial Tire[] Tires { get; set; }

        [Derived]
        public decimal AveragePressure => Tires.Average(t => t.Pressure);

        public Car()
        {
            Tires = Enumerable
                .Range(1, 4)
                .Select(_ => new Tire())
                .ToArray();
        }
    }
}