using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

public class HistoryChartRefreshTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private static (ChartTargetSubject Subject, HistoryChartSeriesInfo[] Series) CreateSeries()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var subject = new ChartTargetSubject(context);
        HistoryChartSeriesInfo[] series =
        [
            new("Temperature", null, subject.TryGetRegisteredProperty(nameof(ChartTargetSubject.Temperature)),
                HistoryChartSeriesKind.Line, StateUnit.DegreeCelsius, HasHistory: true, IsCumulative: false),
            new("Power", null, subject.TryGetRegisteredProperty(nameof(ChartTargetSubject.IsOn)),
                HistoryChartSeriesKind.Band, StateUnit.Default, HasHistory: true, IsCumulative: false)
        ];
        return (subject, series);
    }

    [Fact]
    public void WhenTheValuesAreConstantFromTheStart_ThenTheFirstQueryIsFollowedByExactlyOneQuery()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var queries = 0;
        var (_, series) = CreateSeries();
        using var refresh = new HistoryChartRefresh(timeProvider, () => queries++);
        refresh.SetSeries(series);

        // Act
        var armedAfterFirstQuery = refresh.OnFirstQueryCompleted(Window);
        var armedByRenders = Enumerable.Range(0, 3).Select(_ => refresh.OnRendered(Window)).ToArray();
        timeProvider.Advance(Window);
        var queriesAfterFirstWindow = queries;
        var armedByLaterRenders = Enumerable.Range(0, 3).Select(_ => refresh.OnRendered(Window)).ToArray();
        timeProvider.Advance(Window * 3);

        // Assert
        Assert.True(armedAfterFirstQuery);
        Assert.All(armedByRenders, armed => Assert.False(armed));
        Assert.Equal(1, queriesAfterFirstWindow);
        Assert.All(armedByLaterRenders, armed => Assert.False(armed));
        Assert.Equal(1, queries);
    }

    [Fact]
    public void WhenAQueryCompletesWhileAWindowRuns_ThenTheNextQueryFollowsOneFullWindowOfTheNewLengthLater()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var queries = 0;
        var (subject, series) = CreateSeries();
        using var refresh = new HistoryChartRefresh(timeProvider, () => queries++);
        refresh.SetSeries(series);
        subject.Temperature = 21.5;
        refresh.OnRendered(Window);
        timeProvider.Advance(Window / 2);
        var longerWindow = TimeSpan.FromHours(1);

        // Act
        refresh.SetSeries(series);
        var armed = refresh.OnFirstQueryCompleted(longerWindow);
        timeProvider.Advance(longerWindow - TimeSpan.FromTicks(1));
        var queriesBeforeTheEnd = queries;
        timeProvider.Advance(TimeSpan.FromTicks(1));

        // Assert
        Assert.True(armed);
        Assert.Equal(0, queriesBeforeTheEnd);
        Assert.Equal(1, queries);
    }

    [Fact]
    public void WhenAValueOfAnotherPropertyChanges_ThenNoQueryFollows()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var queries = 0;
        var (subject, series) = CreateSeries();
        using var refresh = new HistoryChartRefresh(timeProvider, () => queries++);
        refresh.SetSeries(series);

        // Act
        subject.Energy = 5;
        subject.NotRecorded = 7;
        var armed = refresh.OnRendered(Window);
        timeProvider.Advance(Window);

        // Assert
        Assert.False(armed);
        Assert.Equal(0, queries);
    }

    [Fact]
    public void WhenAValueOfASeriesChanges_ThenOneQueryFollowsAtTheEndOfTheWindow()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var queries = 0;
        var (subject, series) = CreateSeries();
        using var refresh = new HistoryChartRefresh(timeProvider, () => queries++);
        refresh.SetSeries(series);

        // Act
        subject.Temperature = 21.5;
        var armed = refresh.OnRendered(Window);
        var armedAgain = refresh.OnRendered(Window);
        timeProvider.Advance(Window);

        // Assert
        Assert.True(armed);
        Assert.False(armedAgain);
        Assert.Equal(1, queries);
    }

    [Fact]
    public void WhenAValueChangesWhileAWindowRuns_ThenTheRenderAfterItsQueryStartsExactlyOneMoreWindow()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var queries = 0;
        var (subject, series) = CreateSeries();
        using var refresh = new HistoryChartRefresh(timeProvider, () => queries++);
        refresh.SetSeries(series);
        refresh.OnFirstQueryCompleted(Window);

        // Act
        subject.Temperature = 21.5;
        var armedWhileRunning = refresh.OnRendered(Window);
        timeProvider.Advance(Window);
        var queriesAfterFirstWindow = queries;
        var armedAfterQuery = refresh.OnRendered(Window);
        timeProvider.Advance(Window);
        var armedAfterSecondQuery = refresh.OnRendered(Window);
        timeProvider.Advance(Window * 3);

        // Assert
        Assert.False(armedWhileRunning);
        Assert.Equal(1, queriesAfterFirstWindow);
        Assert.True(armedAfterQuery);
        Assert.False(armedAfterSecondQuery);
        Assert.Equal(2, queries);
    }

    [Fact]
    public void WhenTheSeriesAreSet_ThenTheirCurrentValuesStartNoWindow()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var queries = 0;
        var (subject, series) = CreateSeries();
        using var refresh = new HistoryChartRefresh(timeProvider, () => queries++);
        refresh.SetSeries([]);
        subject.Temperature = 30;
        subject.IsOn = true;

        // Act
        refresh.SetSeries(series);
        var armed = refresh.OnRendered(Window);
        timeProvider.Advance(Window);

        // Assert
        Assert.False(armed);
        Assert.Equal(0, queries);
    }
}
