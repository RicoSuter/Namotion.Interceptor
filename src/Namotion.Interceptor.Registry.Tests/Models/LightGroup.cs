namespace Namotion.Interceptor.Registry.Tests.Models;

[MyInterceptorSubject]
public partial class LightGroup
{
    public LightGroup()
    {
        Lights = [];
    }

    public partial List<Light> Lights { get; set; }

    public partial Dictionary<string, Light>? LightsByName { get; set; }
}
