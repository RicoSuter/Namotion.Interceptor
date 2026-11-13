using HomeBlaze.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// Children in a dictionary with integer keys, whose browser path has the key as text.
/// </summary>
[InterceptorSubject]
public partial class IntKeyedChildrenSubject : IConfigurable
{
    public IntKeyedChildrenSubject()
    {
        Items = new Dictionary<int, PlainConfigurableSubject>();
    }

    public partial Dictionary<int, PlainConfigurableSubject> Items { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
