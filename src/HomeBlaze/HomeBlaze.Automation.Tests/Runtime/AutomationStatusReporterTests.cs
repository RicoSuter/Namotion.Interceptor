using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Automation.Tests.Runtime;

public class AutomationStatusReporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WhenInputsAreDroppedRepeatedly_ThenTheErrorIsShownEachTimeAndLoggedOncePerIntervalWithTheCounts()
    {
        // Arrange
        var logger = new MessageLogger();
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var automation = new Automation(new SubjectPathResolver(() => null), TimeProvider.System, new AllowAllAutomationAuthorizer(), provider, logger)
        {
            Name = "Storm"
        };
        var reporter = new AutomationStatusReporter(automation, logger);

        // Act
        reporter.ReportFaults(new QueueFaults(3, 0), Now);
        reporter.ReportFaults(new QueueFaults(4, 0), Now.AddSeconds(10));
        var messageWithinTheInterval = automation.StatusMessage;
        reporter.ReportFaults(new QueueFaults(5, 1), Now + AutomationStatusReporter.FaultLogInterval);

        // Assert
        Assert.Equal("The input queue overflowed: 4 inputs were dropped and the rules were evaluated again.", messageWithinTheInterval);
        Assert.Equal(ServiceStatus.Error, automation.Status);
        Assert.Equal(
        [
            "The automation Storm failed: The input queue overflowed: 3 inputs were dropped and the rules were evaluated again.",
            "The automation Storm failed: The input queue overflowed: 9 inputs were dropped and the rules were evaluated again. A feedback chain of more than 16 inputs was stopped: 1 inputs were dropped."
        ], logger.Messages.ToArray());
    }
}
