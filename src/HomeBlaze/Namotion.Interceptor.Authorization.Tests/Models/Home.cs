using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

[InterceptorSubject]
public partial class Home
{
    public Home()
    {
        Rooms = [];
        Areas = [];
    }

    public partial Dictionary<string, Room> Rooms { get; set; }

    /// <summary>Rooms addressed by their key alone, to test the spellings of an [InlinePaths] key.</summary>
    [InlinePaths]
    public partial Dictionary<string, Room> Areas { get; set; }

    public partial Wifi? Wifi { get; set; }

    /// <summary>A second parent for a light, to test subjects with several paths.</summary>
    public partial Light? Spare { get; set; }
}
