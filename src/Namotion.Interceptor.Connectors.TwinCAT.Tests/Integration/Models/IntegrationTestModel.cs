using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.TwinCAT.Attributes;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Integration.Models;

[InterceptorSubject]
public partial class IntegrationTestModel
{
    [AdsVariable("GVL.Temperature")]
    public partial double Temperature { get; set; }

    [AdsVariable("GVL.MachineName")]
    public partial string? MachineName { get; set; }

    [AdsVariable("GVL.IsRunning")]
    public partial bool IsRunning { get; set; }

    [AdsVariable("GVL.Counter")]
    public partial int Counter { get; set; }
}
