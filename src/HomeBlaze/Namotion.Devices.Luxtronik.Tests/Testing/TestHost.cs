using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

[InterceptorSubject]
public partial class TestHost
{
    public TestHost()
    {
        HeatPump = null;
    }

    public partial LuxtronikHeatPump? HeatPump { get; set; }

    public static (LuxtronikHeatPump HeatPump, IInterceptorSubjectContext Context) CreateAttachedHeatPump()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var heatPump = new LuxtronikHeatPump(NullLogger<LuxtronikHeatPump>.Instance);
        _ = new TestHost(context) { HeatPump = heatPump };
        return (heatPump, context);
    }
}
