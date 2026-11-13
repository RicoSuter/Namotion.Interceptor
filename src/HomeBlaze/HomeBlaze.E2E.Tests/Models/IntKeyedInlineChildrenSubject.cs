using HomeBlaze.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// Children in an [InlinePaths] dictionary with integer keys, whose browser path is the key as text alone.
/// </summary>
[InterceptorSubject]
public partial class IntKeyedInlineChildrenSubject : IConfigurable
{
    public IntKeyedInlineChildrenSubject()
    {
        Items = new Dictionary<int, PlainConfigurableSubject>();
    }

    [InlinePaths]
    public partial Dictionary<int, PlainConfigurableSubject> Items { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
