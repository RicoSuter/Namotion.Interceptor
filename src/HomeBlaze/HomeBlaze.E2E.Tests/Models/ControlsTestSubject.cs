using HomeBlaze.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// A subject whose widget, <see cref="ControlsTestWidget"/>, has a slider, a text field, a button and an area that scrolls,
/// which no widget of the application has while a page is viewed. The widget shows the values that the controls change.
/// </summary>
[InterceptorSubject]
public partial class ControlsTestSubject : IConfigurable
{
    public partial int Level { get; set; }

    public partial string? Text { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
