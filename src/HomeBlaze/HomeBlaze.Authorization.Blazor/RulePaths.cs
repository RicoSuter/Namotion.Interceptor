using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Authorization.Blazor;

/// <summary>
/// The paths of members and subjects as the path patterns of rules spell them, and the members a rule can select.
/// </summary>
internal static class RulePaths
{
    /// <summary>
    /// Gets the path of a member as path patterns match it, such as <c>/Devices/Kitchen/Temperature</c>, or null when
    /// its subject has no path. Of a subject with several paths, the first is given.
    /// </summary>
    public static string? GetMemberPath(RegisteredSubjectProperty member, PathProviderBase pathProvider)
    {
        var subjectPath = GetSubjectPathPrefix(member.Parent, pathProvider);

        // As the policy names a member: the provider's segment, or the browse name when the provider gives none.
        return subjectPath is null
            ? null
            : string.Concat(subjectPath, pathProvider.PathSeparator.ToString(), pathProvider.TryGetPropertySegment(member) ?? member.BrowseName);
    }

    /// <summary>
    /// Gets the pattern a path picked in the subject tree, a canonical path, starts as: <c>/**</c> appended to the path of
    /// a subject or of a member that can hold subjects, so that it covers what is below; the path of any other member,
    /// as patterns spell it; or the path as given when it names neither.
    /// </summary>
    public static string GetPickedPattern(string path, SubjectPathResolver pathResolver, PathProviderBase pathProvider)
    {
        var separator = pathProvider.PathSeparator.ToString();
        if (pathResolver.ResolveSubject(path, PathStyle.Canonical)?.TryGetRegisteredSubject() is { } subject &&
            GetSubjectPathPrefix(subject, pathProvider) is { } subjectPath)
        {
            return string.Concat(subjectPath, separator, "**");
        }

        if (ResolveMember(path, pathResolver) is { } member && GetMemberPath(member, pathProvider) is { } memberPath)
        {
            return member.CanContainSubjects ? string.Concat(memberPath, separator, "**") : memberPath;
        }

        return path;
    }

    /// <summary>
    /// Gets the member that a canonical path names, as the subject tree writes it: the path of its subject, a slash and
    /// the member's name; or null.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveMember(string path, SubjectPathResolver pathResolver)
    {
        var separatorIndex = path.LastIndexOf('/');
        if (separatorIndex < 0 || separatorIndex == path.Length - 1)
        {
            return null;
        }

        var subjectPath = separatorIndex == 0 ? "/" : path[..separatorIndex];
        return pathResolver.ResolveSubject(subjectPath, PathStyle.Canonical)?.TryGetRegisteredSubject()?.TryGetProperty(path[(separatorIndex + 1)..]);
    }

    /// <summary>
    /// Gets whether a valid path pattern resolves below <paramref name="root"/> to a member that holds no subjects, such
    /// as a value, a method or an attribute, so that nothing is below it.
    /// </summary>
    public static bool NamesValue(string pattern, IInterceptorSubject? root, PathProviderBase pathProvider)
    {
        if (root?.TryGetRegisteredSubject() is not { } registeredRoot)
        {
            return false;
        }

        try
        {
            return pathProvider.TryGetPropertyFromPath(registeredRoot, pattern) is { Index: null, Property.CanContainSubjects: false };
        }
        catch (InvalidOperationException)
        {
            // The graph changed while it was read; the pattern is then not known to name a value.
            return false;
        }
    }

    /// <summary>
    /// Gets whether the member is a registry attribute that marks a property as state or configuration, which describes
    /// the property to HomeBlaze rather than holding data of the device. Metadata is no marker, as rules can target it.
    /// </summary>
    public static bool IsMarker(RegisteredSubjectProperty member) =>
        member.IsAttribute && member.AttributeMetadata.AttributeName is KnownAttributes.State or KnownAttributes.Configuration;

    // The path with its leading separator, empty for a subject without parents, or null when it has none.
    private static string? GetSubjectPathPrefix(RegisteredSubject subject, PathProviderBase pathProvider)
    {
        var parents = subject.Parents;
        if (parents.Length == 0)
        {
            return string.Empty;
        }

        var parent = parents[0];
        var path = parent.Property.TryGetPath(pathProvider, rootSubject: null, parent.Index);
        return path is null ? null : pathProvider.PathSeparator + path;
    }
}
