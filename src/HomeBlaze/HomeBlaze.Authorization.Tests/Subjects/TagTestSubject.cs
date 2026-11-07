using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Authorization.Tests.Subjects;

/// <summary>
/// A subject with one member per HomeBlaze marker that the tag provider maps to tags.
/// </summary>
[InterceptorSubject]
public partial class TagTestSubject
{
    [State]
    public partial double Temperature { get; set; }

    [PropertyAttribute(nameof(Temperature), KnownAttributes.Minimum)]
    public partial double Temperature_Minimum { get; set; }

    [Configuration]
    public partial string? Host { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    public partial TagTestSubject? Child { get; set; }

    public partial string? Plain { get; set; }

    [Derived]
    public string? Title => Host;

    [Query]
    public int GetCount() => 1;

    [Operation]
    public Task StartAsync() => Task.CompletedTask;

    [Operation(RequiresConfirmation = true)]
    public Task ResetAsync() => Task.CompletedTask;
}
