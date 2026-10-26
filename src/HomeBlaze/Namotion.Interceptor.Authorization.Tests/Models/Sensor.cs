using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

[InterceptorSubject]
public partial class Sensor
{
    public Sensor()
    {
        Temperature_Unit = "°C";
    }

    public partial double Temperature { get; set; }

    [PropertyAttribute(nameof(Temperature), "Unit")]
    public partial string Temperature_Unit { get; set; }
}
