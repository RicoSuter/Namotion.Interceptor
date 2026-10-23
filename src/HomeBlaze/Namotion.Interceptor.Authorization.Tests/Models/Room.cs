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

    /// <summary>A second reference, so two rooms can point at each other through two properties.</summary>
    public partial Room? Opposite { get; set; }
}
