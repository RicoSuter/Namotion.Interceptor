using HomeBlaze.Automation.Blazor.Editing;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Blazor.Tests.Testing;

/// <summary>Automations outside a graph, for tests of the editor that need their configuration only.</summary>
internal static class TestAutomations
{
    public static Automation Create(Action<Automation>? configure = null)
    {
        var automation = new Automation(
            new SubjectPathResolver(() => null),
            TimeProvider.System,
            new AllowAll(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<Automation>.Instance);
        configure?.Invoke(automation);
        return automation;
    }

    /// <summary>The expressions of a rule with the key <c>rule</c> of a new automation.</summary>
    public static RuleExpressions Expressions(bool allowsEvent = false) => new(Create(), "rule", allowsEvent);

    private sealed class AllowAll : IAutomationAuthorizer
    {
        public string? GetOperationDenial(IInterceptorSubject automation, RegisteredSubjectProperty operation) => null;

        public string? GetPropertyWriteDenial(IInterceptorSubject automation, RegisteredSubjectProperty property) => null;
    }
}
