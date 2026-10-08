using Namotion.Interceptor.Attributes;

namespace Coffee;

#region WaterTank
[InterceptorSubject]
public partial class WaterTank
{
    public const double CapacityInMilliliters = 1500;

    public partial double Level { get; set; }

    [Derived]
    public bool IsLow => Level < 10;

    public WaterTank()
    {
        Level = 100;
    }
}
#endregion
