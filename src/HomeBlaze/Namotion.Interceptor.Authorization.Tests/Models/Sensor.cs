using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

[InterceptorSubject]
public partial class Sensor
{
    public Sensor()
    {
        Temperature_Unit = "°C";
        Temperature_Unit_Source = "datasheet";
    }

    public partial double Temperature { get; set; }

    [PropertyAttribute(nameof(Temperature), "Unit")]
    public partial string Temperature_Unit { get; set; }

    /// <summary>An attribute of the Unit attribute, to test attributes of attributes.</summary>
    [PropertyAttribute(nameof(Temperature_Unit), "Source")]
    public partial string Temperature_Unit_Source { get; set; }

    /// <summary>An attribute that holds a subject, to test subjects below an attribute.</summary>
    [PropertyAttribute(nameof(Temperature), "Probe")]
    public partial Light? Temperature_Probe { get; set; }
}
