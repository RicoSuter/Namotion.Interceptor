using HomeBlaze.Abstractions.Attributes;
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

    /// <summary>
    /// Has the name of <see cref="NestedConfigurationChild.ShownLabel"/>, so that a link to that property of a child
    /// matches a row of the list too.
    /// </summary>
    [State]
    public partial string? ShownLabel { get; set; }
}
