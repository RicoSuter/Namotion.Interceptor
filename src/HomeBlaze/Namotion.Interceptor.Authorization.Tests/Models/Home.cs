using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

[InterceptorSubject]
public partial class Home
{
    public Home()
    {
        Rooms = [];
    }

    public partial Dictionary<string, Room> Rooms { get; set; }

    public partial Wifi? Wifi { get; set; }

    /// <summary>A second parent for a light, to test subjects with several paths.</summary>
    public partial Light? Spare { get; set; }
}
