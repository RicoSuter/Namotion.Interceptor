using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.TwinCAT.Attributes;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Models;

[InterceptorSubject]
public partial class TestPlcModel
{
    [AdsVariable("GVL.Temperature")]
    public partial double Temperature { get; set; }

    [AdsVariable("GVL.Timestamp")]
    public partial DateTimeOffset Timestamp { get; set; }

    [AdsVariable("GVL.Name")]
    public partial string? Name { get; set; }

    [AdsVariable("GVL.Counter")]
    public partial int Counter { get; set; }

    [AdsVariable("GVL.Pressure")]
    public partial float Pressure { get; set; }

    [AdsVariable("GVL.Values")]
    public partial int[]? Values { get; set; }
}
