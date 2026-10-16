using Namotion.Interceptor.Attributes;

namespace Coffee;

#region Pump
[InterceptorSubject]
public partial class Pump
{
    public partial double Pressure { get; internal set; }

    public partial bool IsRunning { get; internal set; }
}
#endregion
