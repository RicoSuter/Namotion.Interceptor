using HomeBlaze.Automation.Blazor.Editing;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Automation.Blazor.Tests.Testing;

/// <summary>Automations outside a graph, for tests of the editor that need their configuration only.</summary>
internal static class TestAutomations
{
    public static Automation Create(Action<Automation>? configure = null, IAutomationAuthorizer? authorizer = null)
    {
        var automation = new Automation(
            new SubjectPathResolver(() => null),
            TimeProvider.System,
            authorizer ?? new AllowAllAutomationAuthorizer(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<Automation>.Instance);
        configure?.Invoke(automation);
        return automation;
    }

    /// <summary>The expressions of a rule with the key <c>rule</c> of a new automation.</summary>
    public static RuleExpressions Expressions(bool allowsEvent = false) => new(Create(), "rule", allowsEvent);
}

/// <summary>Permissions that refuse every call and write with an error, as while the roles cannot be used.</summary>
internal sealed class UnusableRolesAuthorizer(string error) : IAutomationAuthorizer
{
    public IAutomationPermissions GetPermissions(IReadOnlyList<string> roles) => new UnusablePermissions(error);

    private sealed class UnusablePermissions(string error) : IAutomationPermissions
    {
        public string? Error { get; } = error;

        public string? GetOperationDenial(Namotion.Interceptor.Registry.Abstractions.RegisteredSubjectProperty operation) => Error;

        public string? GetPropertyWriteDenial(Namotion.Interceptor.Registry.Abstractions.RegisteredSubjectProperty property) => Error;
    }
}
