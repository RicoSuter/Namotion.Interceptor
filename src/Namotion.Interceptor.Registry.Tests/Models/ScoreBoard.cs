using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Models;

[InterceptorSubject]
public partial class ScoreBoard
{
    public ScoreBoard()
    {
        Entries = new Dictionary<double, Person>();
    }

    public partial Dictionary<double, Person> Entries { get; set; }
}
