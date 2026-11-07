using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using HomeBlaze.Services.Metadata;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Authorization;

/// <summary>
/// Tags members from the HomeBlaze markers, see <see cref="MemberTags"/>: state, configuration and secrets, queries,
/// operations and whether they require confirmation, properties holding subjects, attributes and metadata.
/// </summary>
/// <remarks>
/// Thread-safe; returns a shared array per combination of tags. The tags of a member are
/// cached until an attribute is added to it. Only a property of type <see cref="MethodMetadata"/> is read.
/// </remarks>
public sealed class HomeBlazeMemberTagProvider : IMemberTagProvider
{
    private static readonly string[]?[] TagsByMarkers = new string[]?[1 << MemberTags.All.Length];

    // In the order of MemberTags.All, whose tag of each bit is given.
    [Flags]
    private enum Markers
    {
        None = 0,
        State = 1 << 0,
        Configuration = 1 << 1,
        Secret = 1 << 2,
        Query = 1 << 3,
        Operation = 1 << 4,
        RequiresConfirmation = 1 << 5,
        Subjects = 1 << 6,
        Attribute = 1 << 7,
        Metadata = 1 << 8,
        Administration = 1 << 9
    }

    // The tags of each member with the number of its attributes when they were computed: markers are attributes
    // registered while a subject attaches, so a changed number computes them again.
    private static readonly ConditionalWeakTable<RegisteredSubjectProperty, CachedTags> TagsByMember = new();

    /// <inheritdoc />
    public ImmutableArray<string> GetTags(RegisteredSubjectProperty member)
    {
        var attributeCount = member.Attributes.Length;
        if (TagsByMember.TryGetValue(member, out var cached) && cached.AttributeCount == attributeCount)
        {
            return cached.Tags;
        }

        var index = (int)GetMarkers(member);
        var tags = ImmutableCollectionsMarshal.AsImmutableArray(Volatile.Read(ref TagsByMarkers[index]) ?? CreateTags(index));
        TagsByMember.AddOrUpdate(member, new CachedTags(tags, attributeCount));
        return tags;
    }

    private static Markers GetMarkers(RegisteredSubjectProperty member)
    {
        var markers = Markers.None;
        if (member.TryGetAttribute(KnownAttributes.State) is not null)
        {
            markers |= Markers.State;
        }

        if (member.TryGetAttribute(KnownAttributes.Configuration) is { } configuration)
        {
            markers |= Markers.Configuration;
            if (configuration.GetValue() is ConfigurationMetadata { IsSecret: true })
            {
                markers |= Markers.Secret;
            }
        }

        markers |= GetMethodMarkers(member);

        if (member.CanContainSubjects)
        {
            markers |= Markers.Subjects;
        }

        if (member.IsAttribute)
        {
            markers |= Markers.Attribute;
        }

        if (member.IsMetadata())
        {
            markers |= Markers.Metadata;
        }

        return markers;
    }

    // Only a property of the type that methods are registered with is read, so that a getter of another property, which
    // may be expensive or throw, is never called.
    private static Markers GetMethodMarkers(RegisteredSubjectProperty member)
    {
        if (member.Type != typeof(MethodMetadata) || member.GetValue() is not MethodMetadata method)
        {
            return Markers.None;
        }

        if (method.Kind == MethodKind.Query)
        {
            return Markers.Query;
        }

        var markers = method.RequiresConfirmation ? Markers.Operation | Markers.RequiresConfirmation : Markers.Operation;
        return method.RequiresAdministration ? markers | Markers.Administration : markers;
    }

    private static string[] CreateTags(int index)
    {
        var tags = new List<string>(MemberTags.All.Length);
        for (var bit = 0; bit < MemberTags.All.Length; bit++)
        {
            if ((index & (1 << bit)) != 0)
            {
                tags.Add(MemberTags.All[bit]);
            }
        }

        var created = tags.ToArray();
        return Interlocked.CompareExchange(ref TagsByMarkers[index], created, null) ?? created;
    }

    private sealed record CachedTags(ImmutableArray<string> Tags, int AttributeCount);
}
