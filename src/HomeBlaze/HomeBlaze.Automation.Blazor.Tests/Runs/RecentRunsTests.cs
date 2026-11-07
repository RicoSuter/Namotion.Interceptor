using HomeBlaze.Automation.Blazor.Runs;
using HomeBlaze.Automation.Blazor.Tests.Testing;
using HomeBlaze.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Blazor.Tests.Runs;

public class RecentRunsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    // The run properties are set by the runtime only, so a test sets them through the property metadata of the subject.
    private static void SetRunProperty(AutomationRule rule, string propertyName, object? value) =>
        ((IInterceptorSubject)rule).Properties[propertyName].SetValue!(rule, value);

    private static (Automation Automation, AutomationRule Finished) CreateAutomation()
    {
        var started = new AutomationRule { Name = "Started" };
        var ran = new AutomationRule { Name = "Finished" };
        SetRunProperty(ran, nameof(AutomationRule.RunCount), 2L);
        SetRunProperty(ran, nameof(AutomationRule.LastRunOutcome), AutomationRunOutcome.DryRun);
        SetRunProperty(ran, nameof(AutomationRule.LastRunMessage), "Would run ChangeState(Idle)");
        var automation = TestAutomations.Create(created => created.Rules = new Dictionary<string, AutomationRule>
        {
            ["started"] = started,
            ["finished"] = ran
        });
        return (automation, ran);
    }

    [Fact]
    public async Task WhenTheGraphHasNoHistoryStore_ThenTheLastRunOfEachRuleThatRanIsListedWithItsCaption()
    {
        // Arrange
        var (automation, _) = CreateAutomation();

        // Act
        var result = await RecentRuns.LoadAsync(automation, new SubjectPathResolver(() => null), Now, CancellationToken.None);

        // Assert
        Assert.False(result.HasHistory);
        Assert.Null(result.Error);
        Assert.False(result.IsBehind);
        Assert.Equal("Without a history store: the last run of each rule.", result.Caption);
        Assert.Equal([new RunEntry(null, "Finished", AutomationRunOutcome.DryRun, "Would run ChangeState(Idle)")], result.Runs.ToArray());
    }

    [Fact]
    public async Task WhenARuleRunsAgainWithoutAHistoryStore_ThenTheKeyChangesAndTheNextLoadListsTheNewRun()
    {
        // Arrange
        var (automation, finished) = CreateAutomation();
        var keyBefore = RecentRuns.GetKey(automation);

        // Act
        SetRunProperty(finished, nameof(AutomationRule.RunCount), 3L);
        SetRunProperty(finished, nameof(AutomationRule.LastRunOutcome), AutomationRunOutcome.Succeeded);
        SetRunProperty(finished, nameof(AutomationRule.LastRunMessage), "Ran ChangeState(Idle)");
        var keyAfter = RecentRuns.GetKey(automation);
        var result = await RecentRuns.LoadAsync(automation, new SubjectPathResolver(() => null), Now, CancellationToken.None);

        // Assert
        Assert.NotEqual(keyBefore, keyAfter);
        Assert.Equal([new RunEntry(null, "Finished", AutomationRunOutcome.Succeeded, "Ran ChangeState(Idle)")], result.Runs.ToArray());
    }

    [Fact]
    public void WhenTheRunsComeFromHistory_ThenTheCaptionSaysSoAndNamesWhatHistoryCanMiss()
    {
        // Act
        var result = new RecentRunsResult(true, [], null, false);

        // Assert
        Assert.Equal("From history: the runs of the last seven days. Runs within about 250 ms can show as one, and a crash can drop runs.", result.Caption);
    }
}
