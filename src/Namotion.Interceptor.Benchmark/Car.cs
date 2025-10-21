using System.Linq;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Benchmark
{
    [InterceptorSubject]
    public partial class Car
    {
        public Car()
        {
            Tires =
            [
                new Tire(),
                new Tire(),
                new Tire(),
                new Tire()
            ];

            Name = "My Car";
            Name_MaxLength = 123;
            Name_MaxLength_Unit = "Count";
        }

        public partial string Name { get; set; }
        
        [PropertyAttribute(nameof(Name_MaxLength), "Unit")]
        public partial string Name_MaxLength_Unit { get; set; }
        
        [PropertyAttribute(nameof(Name), "MaxLength")]
        public partial int Name_MaxLength { get; set; }

        public partial Tire[] Tires { get; set; }

        public partial Car[]? PreviousCars { get; set; }

        public decimal AveragePressure => Tires.Average(t => t.Pressure);
    }
}