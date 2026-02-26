using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.TwinCAT.Attributes;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Integration.Models;

[InterceptorSubject]
public partial class PolledIntegrationTestModel
{
    [AdsVariable("GVL.PolledCounter", ReadMode = AdsReadMode.Polled)]
    public partial int PolledCounter { get; set; }
}
