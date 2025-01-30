using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Benchmark
{
    [InterceptorSubject]
    public partial class Tire
    {
        public Tire()
        {
            Pressure_Minimum = 0.0m;
            Pressure_Minimum = 4.0m;
        }

        public partial decimal Pressure { get; set; }

        public partial decimal Pressure_Minimum { get; set; }

        public partial decimal Pressure_Maximum { get; set; }
    }
}