using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// A list of children that a test changes while the subject browser shows one of them.
/// </summary>
[InterceptorSubject]
public partial class ChildListSubject
{
    public ChildListSubject()
    {
        Items = [];
    }

    public partial NestedConfigurationSubject[] Items { get; set; }
}
