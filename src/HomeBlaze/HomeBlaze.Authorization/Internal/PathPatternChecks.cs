using System.Collections.Immutable;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Authorization.Internal;

/// <summary>Checks path patterns: whether one is valid, and whether one or those of a role name subjects instead of members.</summary>
internal static class PathPatternChecks
{
    private static readonly HomeBlazeMemberTagProvider TagProvider = new();

    // The name of the role a pattern is validated in, removed from the error since the pattern belongs to no role.
    private const string PatternValidationRoleName = "PatternValidation";

    /// <summary>
    /// Gets why a path pattern is invalid, or null when it is valid, by compiling a role with one rule holding it, as
    /// the library parses patterns only while compiling.
    /// </summary>
    public static string? Validate(string pattern, PathProviderBase pathProvider)
    {
        var role = new AuthorizationRole
        {
            Name = PatternValidationRoleName,
            Allow = [new PermissionRule { Paths = [pattern], Actions = [PermissionAction.Read] }]
        };

        try
        {
            AuthorizationPolicy.Compile([role], [PatternValidationRoleName], [], [], TagProvider, pathProvider);
            return null;
        }
        catch (AuthorizationException exception)
        {
            return exception.Message.Replace($" of role '{PatternValidationRoleName}'", "", StringComparison.Ordinal);
        }
    }

    // A pattern without a wildcard that resolves to an entry of a dictionary or collection, such as a device in a folder,
    // names a subject that has no member of its own and matches nothing, which the policy cannot tell without the graph.
    // The graph can change while it is read, as in AuthorizationService.BuildSnapshot; the warning is then left out.
    public static string? GetWarning(
        AuthorizationRole role,
        IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> knownSubjects,
        PathProviderBase pathProvider,
        ref List<RegisteredSubject>? roots)
    {
        List<string>? entryPatterns = null;
        try
        {
            AddEntryPatterns(role.Allow, knownSubjects, pathProvider, ref roots, ref entryPatterns);
            AddEntryPatterns(role.Deny, knownSubjects, pathProvider, ref roots, ref entryPatterns);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (entryPatterns is null)
        {
            return null;
        }

        return string.Join(' ', entryPatterns.Select(pattern => FormatWarning(pattern, pathProvider.PathSeparator)));
    }

    /// <summary>
    /// Gets the warning of one valid path pattern that names an entry below one of <paramref name="roots"/>, as
    /// <see cref="GetWarning(AuthorizationRole, IReadOnlyDictionary{IInterceptorSubject, RegisteredSubject}, PathProviderBase, ref List{RegisteredSubject}?)"/>
    /// gives it for a role, or null.
    /// </summary>
    public static string? GetWarning(string pattern, List<RegisteredSubject> roots, PathProviderBase pathProvider)
    {
        if (pattern.Contains('*'))
        {
            return null;
        }

        try
        {
            return NamesEntry(pattern, roots, pathProvider) ? FormatWarning(pattern, pathProvider.PathSeparator) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Gets the subjects without parents among <paramref name="knownSubjects"/>, which patterns are resolved below.</summary>
    public static List<RegisteredSubject> GetRoots(IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> knownSubjects) =>
        knownSubjects.Values.Where(static subject => subject.Parents.Length == 0).ToList();

    private static string FormatWarning(string pattern, char separator) =>
        $"Path pattern '{pattern}' names a subject, not a member, and matches nothing; use '{pattern}{separator}**' for the subject and everything below it, or '{pattern}{separator}*' for its members.";

    private static void AddEntryPatterns(
        ImmutableArray<PermissionRule> rules,
        IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> knownSubjects,
        PathProviderBase pathProvider,
        ref List<RegisteredSubject>? roots,
        ref List<string>? entryPatterns)
    {
        foreach (var rule in rules)
        {
            if (rule.Paths.IsDefault)
            {
                continue;
            }

            foreach (var pattern in rule.Paths)
            {
                if (pattern.Contains('*') || entryPatterns?.Contains(pattern) == true)
                {
                    continue;
                }

                roots ??= GetRoots(knownSubjects);
                if (NamesEntry(pattern, roots, pathProvider))
                {
                    (entryPatterns ??= []).Add(pattern);
                }
            }
        }
    }

    // A pattern matches the member paths below every root, so it names an entry when it resolves to an entry holding a
    // subject below one root and to a member below none.
    private static bool NamesEntry(string pattern, List<RegisteredSubject> roots, PathProviderBase pathProvider)
    {
        var namesEntry = false;
        foreach (var root in roots)
        {
            if (pathProvider.TryGetPropertyFromPath(root, pattern) is not { } resolved)
            {
                continue;
            }

            if (resolved.Index is null)
            {
                return false;
            }

            namesEntry |= pathProvider.TryGetSubjectFromPath(root, pattern) is not null;
        }

        return namesEntry;
    }
}
