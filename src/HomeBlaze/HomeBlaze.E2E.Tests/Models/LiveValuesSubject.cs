using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// Values that a test changes on the server while a page shows them, to check that every widget shows the change.
/// </summary>
[InterceptorSubject]
public partial class LiveValuesSubject : IConfigurable
{
    public partial int Level { get; set; }

    public partial string? Text { get; set; }

    public partial bool IsOn { get; set; }

    [PropertyAttribute(nameof(Run), KnownAttributes.IsEnabled)]
    public bool Run_IsEnabled => IsOn;

    [Operation(Title = "Run")]
    public void Run()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
