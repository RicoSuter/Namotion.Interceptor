using System.Collections.Generic;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Benchmark;

[InterceptorSubject]
public partial class Garage
{
    public Garage()
    {
        ByName = new Dictionary<string, Car>();
        ById = new Dictionary<int, Car>();
        Spots = new Dictionary<string, Car>();
    }

    public partial Dictionary<string, Car> ByName { get; set; }

    public partial Dictionary<int, Car> ById { get; set; }

    [InlinePaths]
    public partial Dictionary<string, Car> Spots { get; set; }
}
