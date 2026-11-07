using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components.Tests.Models;

/// <summary>
/// A subject with a value and a list of subjects in its configuration.
/// </summary>
[InterceptorSubject]
public partial class SubjectHoldingSubject
{
    [Configuration]
    public partial string? Label { get; set; }

    [Configuration]
    public partial List<EditedSubject> Items { get; set; }

    public SubjectHoldingSubject()
    {
        Items = [];
    }
}
