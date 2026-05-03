namespace Namotion.Interceptor.Registry.Tests.Models;

[MyInterceptorSubject]
public partial class DimmableLight : Light
{
    public partial double Brightness { get; set; }
}
