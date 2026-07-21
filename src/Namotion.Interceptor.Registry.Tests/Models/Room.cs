namespace Namotion.Interceptor.Registry.Tests.Models;

[MyInterceptorSubject]
public partial class Room
{
    public partial Light? Light { get; set; }

    public partial Zone? Zone { get; set; }
}
