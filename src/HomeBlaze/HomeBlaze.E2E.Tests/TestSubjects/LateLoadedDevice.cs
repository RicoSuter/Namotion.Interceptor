using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.TestSubjects;

/// <summary>
/// A subject type the server does not know at startup; a test adds it to stand in for a plugin loaded at runtime.
/// </summary>
[InterceptorSubject]
public partial class LateLoadedDevice : IConfigurable, ITitleProvider
{
    public LateLoadedDevice()
    {
        Name = string.Empty;
    }

    [Configuration]
    public partial string Name { get; set; }

    [Derived]
    public string? Title => Name;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
