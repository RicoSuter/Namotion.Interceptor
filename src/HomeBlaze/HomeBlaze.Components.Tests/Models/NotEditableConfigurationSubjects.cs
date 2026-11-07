using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components.Tests.Models;

/// <summary>
/// A subject whose only configuration property holds subjects.
/// </summary>
[InterceptorSubject]
public partial class SubjectsOnlyHoldingSubject
{
    [Configuration]
    public partial List<EditedSubject> Items { get; set; }

    public SubjectsOnlyHoldingSubject()
    {
        Items = [];
    }
}

/// <summary>
/// A subject whose only configuration property is a slot.
/// </summary>
[InterceptorSubject]
public partial class SlotOnlySubject
{
    [Configuration]
    [Slot]
    public partial string? Text { get; set; }
}

/// <summary>
/// A subject whose only configuration property has no setter.
/// </summary>
[InterceptorSubject]
public partial class InitOnlySubject
{
    [Configuration]
    public partial string? Text { get; init; }
}
