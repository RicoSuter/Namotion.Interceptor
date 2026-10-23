using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

[InterceptorSubject]
public partial class Room
{
    public Room()
    {
        Lights = [];
    }

    public partial Dictionary<string, Light> Lights { get; set; }

    public partial Sensor? Sensor { get; set; }

    /// <summary>A reference that can form a cycle between rooms.</summary>
    public partial Room? Neighbor { get; set; }
}
