using System.Collections.Immutable;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>Gives every member no tags.</summary>
internal sealed class EmptyTagProvider : IMemberTagProvider
{
    public static EmptyTagProvider Instance { get; } = new();

    public ImmutableArray<string> GetTags(RegisteredSubjectProperty member) => [];
}
