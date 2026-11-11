using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Authorization.Blazor.Tests;

/// <summary>
/// A device with a state value and its attribute, a secret, a child device and an operation.
/// </summary>
[InterceptorSubject]
public partial class TestDevice
{
    [State]
    public partial double Temperature { get; set; }

    [PropertyAttribute(nameof(Temperature), KnownAttributes.Minimum)]
    public partial double Temperature_Minimum { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    public partial TestDevice? Child { get; set; }

    [Operation]
    public Task StartAsync() => Task.CompletedTask;
}
