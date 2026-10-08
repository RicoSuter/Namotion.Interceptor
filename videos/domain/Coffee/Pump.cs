using Namotion.Interceptor.Attributes;

namespace Coffee;

#region Pump
[InterceptorSubject]
public partial class Pump
{
    public partial double Pressure { get; set; }

    public partial bool IsRunning { get; set; }
}
#endregion
