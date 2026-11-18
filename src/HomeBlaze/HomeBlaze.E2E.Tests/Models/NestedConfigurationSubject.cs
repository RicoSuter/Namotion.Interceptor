using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// A subject with a subject in its configuration, which a configuration that is loaded again replaces with a new one.
/// </summary>
[InterceptorSubject]
public partial class NestedConfigurationSubject : IConfigurable
{
    [Configuration]
    public partial NestedConfigurationChild? Child { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The subject in the configuration of <see cref="NestedConfigurationSubject"/>, whose pane shows its label as state.
/// </summary>
[InterceptorSubject]
public partial class NestedConfigurationChild
{
    [Configuration]
    public partial string? Label { get; set; }

    [Derived]
    [State]
    public string? ShownLabel => Label;
}
