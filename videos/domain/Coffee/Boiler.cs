using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Attributes;

namespace Coffee;

#region Boiler
[InterceptorSubject]
public partial class Boiler
{
    public partial double Temperature { get; internal set; }

    [Range(85.0, 96.0)]
    public partial double TargetTemperature { get; set; }

    public partial bool HeaterOn { get; internal set; }

    #region IsHot
    [Derived]
    public bool IsHot => Temperature >= TargetTemperature - 1;
    #endregion

    public Boiler()
    {
        Temperature = 20;
        TargetTemperature = 93;
    }
}
#endregion
