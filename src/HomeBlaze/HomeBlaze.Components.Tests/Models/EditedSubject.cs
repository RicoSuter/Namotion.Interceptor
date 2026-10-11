using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components.Tests.Models;

[InterceptorSubject]
public partial class EditedSubject
{
    [Configuration]
    public partial string? Label { get; set; }

    [Configuration]
    public partial int Interval { get; set; }

    [Configuration]
    public partial int? Limit { get; set; }

    [Configuration]
    public partial string[]? Tags { get; set; }

    [Configuration]
    [Range(1, 10)]
    public partial int Level { get; set; }
}
