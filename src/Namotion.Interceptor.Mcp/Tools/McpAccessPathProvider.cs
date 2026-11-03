using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// The path provider of a configuration with an access policy: it includes what the inner provider includes and the
/// policy allows to read, and looks up a segment as the inner provider does, but treats a property the policy does not
/// allow to read as absent. Separators and segments are the inner provider's.
/// </summary>
internal sealed class McpAccessPathProvider : PathProviderBase
{
    private readonly PathProviderBase _inner;
    private readonly IMcpAccessPolicy _policy;

    /// <summary>
    /// <paramref name="inner"/> restricted to what <paramref name="policy"/> allows to read.
    /// </summary>
    internal McpAccessPathProvider(PathProviderBase inner, IMcpAccessPolicy policy)
    {
        _inner = inner;
        _policy = policy;
    }

    /// <summary>
    /// Whether the policy allows to read the member and, for an attribute, the property it belongs to, recursively up
    /// to a property that is no attribute. Attributes that belong to each other in a cycle are not readable.
    /// </summary>
    public static bool IsReadable(IMcpAccessPolicy policy, RegisteredSubjectProperty member)
    {
        // Attributes can name each other in a cycle; a chain longer than the subject's properties is one.
        var remainingSteps = member.Parent.Properties.Length;
        var current = member;
        while (policy.IsAllowed(current, McpAccessActions.Read))
        {
            if (!current.IsAttribute)
            {
                return true;
            }

            current = current.Parent.TryGetProperty(current.AttributeMetadata.PropertyName);
            if (current is null || --remainingSteps < 0)
            {
                return false;
            }
        }

        return false;
    }

    public override char PathSeparator => _inner.PathSeparator;

    public override char IndexOpen => _inner.IndexOpen;

    public override char IndexClose => _inner.IndexClose;

    public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
    {
        if (_inner.TryGetPropertySegment(property, out segment) && IsReadable(_policy, property))
        {
            return true;
        }

        segment = null;
        return false;
    }

    public override bool TryGetPropertyFromSegment(
        RegisteredSubject subject, string segment, [NotNullWhen(true)] out RegisteredSubjectProperty? property)
    {
        if (!_inner.TryGetPropertyFromSegment(subject, segment, out var innerProperty))
        {
            property = null;
            return false;
        }

        if (IsReadable(_policy, innerProperty))
        {
            property = innerProperty;
            return true;
        }

        // A later member with the same segment answers as if the hidden one did not exist. The [InlinePaths] property
        // yields its segment to every other member, so it is matched only below.
        var inlinePathsPropertyName = InlinePathsAttribute.GetInlinePathsPropertyName(subject.Subject.GetType());
        foreach (var member in subject.Properties)
        {
            if (member != innerProperty && member.Name != inlinePathsPropertyName &&
                _inner.TryGetPropertySegment(member, out var memberSegment) && memberSegment == segment &&
                IsReadable(_policy, member))
            {
                property = member;
                return true;
            }
        }

        // As for a name that does not exist, the segment is a key of the [InlinePaths] dictionary, so a client cannot
        // tell the hidden property from a missing one and the registry builds the key without the explicit form.
        if (inlinePathsPropertyName is not null &&
            subject.TryGetProperty(inlinePathsPropertyName) is { } inlinePathsProperty &&
            TryGetPropertySegment(inlinePathsProperty, out _))
        {
            property = inlinePathsProperty;
            return true;
        }

        property = null;
        return false;
    }
}
