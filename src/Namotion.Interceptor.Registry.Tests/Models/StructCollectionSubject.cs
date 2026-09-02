using System.Collections.Immutable;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Models;

/// <summary>
/// Model with struct collection properties that are never assigned, so they hold the default
/// instance whose inner array is null. <see cref="Tags"/> is deliberately not subject-carrying:
/// the JSON writer enumerates every <see cref="System.Collections.IEnumerable"/>, so a default
/// struct reaches it whether or not the property is structural.
/// </summary>
[InterceptorSubject]
public partial class StructCollectionSubject
{
    public partial ImmutableArray<string> Tags { get; set; }

    public partial ImmutableArray<Person> Children { get; set; }
}
