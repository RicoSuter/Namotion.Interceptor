using HomeBlaze.Abstractions;
using Namotion.Interceptor.Dynamic;

namespace HomeBlaze.OpcUa;

public class OpcUaDynamicSubject : DynamicSubject, ITitleProvider
{
    public string? Title { get; }

    public OpcUaDynamicSubject(string? title)
    {
        Title = title;
    }
}
