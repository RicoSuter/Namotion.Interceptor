using HomeBlaze.Services;

namespace HomeBlaze.Automation;

/// <summary>The services every automation takes, resolved from dependency injection when an automation is created.</summary>
public sealed class AutomationServices(
    SubjectPathResolver pathResolver,
    TimeProvider timeProvider,
    IAutomationAuthorizer authorizer,
    IServiceProvider serviceProvider)
{
    public SubjectPathResolver PathResolver { get; } = pathResolver;

    /// <summary>The clock of <c>now</c>, <c>time</c>, holds and timeouts; its local time zone is that of <c>time</c>.</summary>
    public TimeProvider TimeProvider { get; } = timeProvider;

    public IAutomationAuthorizer Authorizer { get; } = authorizer;

    /// <summary>Resolves the services that operations take with <c>[FromServices]</c>.</summary>
    public IServiceProvider ServiceProvider { get; } = serviceProvider;
}
