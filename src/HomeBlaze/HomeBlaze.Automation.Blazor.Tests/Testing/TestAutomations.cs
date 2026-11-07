using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Automation.Blazor.Tests.Testing;

/// <summary>Automations outside a graph, for tests of the editor that need their configuration only.</summary>
internal static class TestAutomations
{
    public static Automation Create(Action<Automation>? configure = null)
    {
        var automation = new Automation(
            new SubjectPathResolver(() => null),
            TimeProvider.System,
            new AllowAllAutomationAuthorizer(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<Automation>.Instance);
        configure?.Invoke(automation);
        return automation;
    }
}
