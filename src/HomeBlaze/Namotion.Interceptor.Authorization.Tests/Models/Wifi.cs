using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

[InterceptorSubject]
public partial class Wifi
{
    public Wifi()
    {
        Ssid = "Home";
        Password = "secret";
    }

    public partial string Ssid { get; set; }

    public partial string Password { get; set; }
}
