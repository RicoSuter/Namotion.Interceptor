using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Authorization.Blazor;

/// <summary>
/// Finds the methods that a policy allows to execute but not to read, which cannot be used: a method is listed and run
/// only when it is readable.
/// </summary>
internal static class ExecuteWithoutRead
{
    /// <summary>Why a method that is executable but not readable cannot be used.</summary>
    public const string Reason = "A method is listed and run only when it is readable.";

    /// <summary>
    /// Gets whether <paramref name="role"/> has an allow rule with execute, without which it is not checked, so that most
    /// roles never compile a policy or walk the graph for it.
    /// </summary>
    public static bool MayApply(AuthorizationRole role) =>
        role.Allow.Any(rule => rule.Actions.Contains(AuthorizationActions.Execute));

    /// <summary>
    /// Gets the methods among the members of <paramref name="subjects"/> that <paramref name="policy"/> allows to execute
    /// but not to read, with an example path in general and for each allow rule of <paramref name="role"/> that has
    /// execute without read and is the first to allow one of them; null when there are none.
    /// </summary>
    public static ExecuteWithoutReadFindings? Find(
        AuthorizationRole role, AuthorizationPolicy policy, IEnumerable<RegisteredSubject> subjects, PathProviderBase pathProvider)
    {
        var count = 0;
        string? examplePath = null;
        var examplePathsByRule = new Dictionary<PermissionRule, string>(ReferenceEqualityComparer.Instance);
        foreach (var subject in subjects)
        {
            foreach (var member in subject.Properties)
            {
                if (!IsExecutableWithoutRead(member, policy))
                {
                    continue;
                }

                var path = RulePaths.GetMemberPath(member, pathProvider) ?? member.Name;
                count++;
                examplePath = GetFirst(examplePath, path);

                // Only the first allowing rule is named, so a rule that allows the method as well but later is not.
                var decision = policy.Explain(member, AuthorizationActions.Execute);
                if (decision.AllowedByRule is { } rule && IsOwnRuleWithoutRead(role, rule))
                {
                    examplePathsByRule[rule] = GetFirst(examplePathsByRule.GetValueOrDefault(rule), path);
                }
            }
        }

        return count == 0 ? null : new ExecuteWithoutReadFindings(count, examplePath!, examplePathsByRule);
    }

    // The example is the first path in ordinal order, so that it does not depend on the order of the subjects.
    private static string GetFirst(string? first, string path) =>
        first is null || string.CompareOrdinal(path, first) < 0 ? path : first;

    // Methods are registered as properties of this type; no other getter is called.
    private static bool IsExecutableWithoutRead(RegisteredSubjectProperty member, AuthorizationPolicy policy) =>
        member.Type == typeof(MethodMetadata) && policy.IsAllowed(member, AuthorizationActions.Execute) && !policy.CanRead(member);

    /// <summary>Gets the hint for an allow rule without read that allows the method at <paramref name="examplePath"/> first.</summary>
    public static string DescribeRule(string examplePath) =>
        $"This rule allows execute without read, so methods it matches, such as {examplePath}, cannot be used. {Reason} Allow read on them as well.";

    private static bool IsOwnRuleWithoutRead(AuthorizationRole role, PermissionRule rule) =>
        !rule.Actions.Contains(PermissionAction.Read) && role.Allow.Any(allowRule => ReferenceEquals(allowRule, rule));
}

/// <summary>The methods that a policy allows to execute but not to read, see <see cref="ExecuteWithoutRead.Find"/>.</summary>
/// <param name="Count">The number of such methods.</param>
/// <param name="ExamplePath">The path of one of them.</param>
/// <param name="ExamplePathsByRule">The path of one of them per allow rule without read that allows it first.</param>
internal sealed record ExecuteWithoutReadFindings(int Count, string ExamplePath, IReadOnlyDictionary<PermissionRule, string> ExamplePathsByRule)
{
    /// <summary>Gets the hint for the role as a whole.</summary>
    public string Describe() =>
        $"Execute is allowed without read on {Count} {(Count == 1 ? "method" : "methods")}, such as {ExamplePath}, which therefore cannot be used. {ExecuteWithoutRead.Reason} Allow read on them as well.";
}
