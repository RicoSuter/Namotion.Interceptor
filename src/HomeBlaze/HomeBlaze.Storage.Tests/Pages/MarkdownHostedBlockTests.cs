using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// A hosted subject runs exactly as long as its block is on the page. A subject that a parse created and did
/// not publish must not keep running.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownHostedBlockTests : IAsyncLifetime
{
    private const string Namespace = "HomeBlaze.Storage.Tests.Pages";
    private const string HostedType = $"{Namespace}.TestHosted";

    private readonly RecordingLogger<MarkdownContentParser> _logger = new();
    private MarkdownPageFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = await MarkdownPageFixture.CreateAsync(
            services => services.AddSingleton<ILogger<MarkdownContentParser>>(_logger));
        await _fixture.StartHostedServicesAsync();
    }

    public Task DisposeAsync() => _fixture.StopHostedServicesAsync();

    private TestHostedLog Log => _fixture.HostedLog;

    private async Task ReloadAsync(MarkdownFile page, string content, int times)
    {
        for (var count = 0; count < times; count++)
        {
            await _fixture.UpdatePageAsync(page, content);
        }
    }

    [Fact]
    public async Task WhenValidBlockIsReloadedAndThenRemoved_ThenItsSubjectStartsOnceAndStopsWithTheBlock()
    {
        // Arrange
        var content = $":: {HostedType}#hosted Name=A\n::";
        var page = await _fixture.AddPageAsync("page.md", content);
        var hosted = page.Children["hosted"];
        await _fixture.SettleHostedServicesAsync();
        var runningAfterLoad = Log.Running;

        // Act
        await ReloadAsync(page, content, times: 3);
        await _fixture.SettleHostedServicesAsync();
        var runningAfterReloads = Log.Running;
        var startCountAfterReloads = Log.StartCount;

        await _fixture.UpdatePageAsync(page, "text");
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.Same(hosted, Assert.Single(runningAfterLoad));
        Assert.Same(hosted, Assert.Single(runningAfterReloads));
        Assert.Equal(1, startCountAfterReloads);
        Assert.Empty(Log.Running);
        Assert.Equal(1, Log.StartCount);
    }

    [Fact]
    public async Task WhenBlockHasContentButItsTypeHasNoDefaultSlot_ThenNoSubjectIsStarted()
    {
        // Arrange
        var content = $":: {HostedType}#hosted Name=B\nSome text.\n::";

        // Act
        var page = await _fixture.AddPageAsync("page.md", content);
        await ReloadAsync(page, content, times: 3);
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.Contains("has no default slot", MarkdownPageFixture.Html(page.Children));
        Assert.Empty(Log.Running);
        Assert.Equal(0, Log.StartCount);
    }

    [Fact]
    public async Task WhenBlockWithContentErrorIsFixed_ThenExactlyOneInstanceRuns()
    {
        // Arrange
        var broken = $":: {HostedType}#hosted Name=B\nSome text.\n::";
        var page = await _fixture.AddPageAsync("page.md", broken);
        await ReloadAsync(page, broken, times: 3);

        // Act
        await _fixture.UpdatePageAsync(page, $":: {HostedType}#hosted Name=B\n::");
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.Same(page.Children["hosted"], Assert.Single(Log.Running));
        Assert.Equal(1, Log.StartCount);
    }

    [Fact]
    public async Task WhenConfigurationOfReusedBlockFailsToApply_ThenItsInstanceIsStopped()
    {
        // Arrange
        var page = await _fixture.AddPageAsync("page.md", $":: {HostedType}#hosted Mode=ok\n::");
        await _fixture.SettleHostedServicesAsync();
        var runningBefore = Log.Running;

        // Act
        await _fixture.UpdatePageAsync(page, $":: {HostedType}#hosted Mode={TestHosted.ThrowMode}\n::");
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.Single(runningBefore);
        Assert.Contains("apply failed", MarkdownPageFixture.Html(page.Children));
        Assert.Empty(Log.Running);
        Assert.Equal(1, Log.StartCount);
    }

    [Fact]
    public async Task WhenConfigurationValueOfNewBlockIsRejectedByItsSetter_ThenNoInstanceKeepsRunning()
    {
        // Arrange
        var content = $":: {HostedType}#hosted Level=99\n::";

        // Act
        var page = await _fixture.AddPageAsync("page.md", content);
        await ReloadAsync(page, content, times: 3);
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.Contains("could not be created or updated", MarkdownPageFixture.Html(page.Children));
        Assert.False(page.Children.ContainsKey("hosted"));
        Assert.Empty(Log.Running);
    }

    [Fact]
    public async Task WhenValueInTheJsonBlockOfNewBlockDoesNotFit_ThenNoInstanceKeepsRunning()
    {
        // Arrange
        var content = $":: {HostedType}#hosted Name=A\n```json\n{{ \"Level\": \"high\" }}\n```\n::";

        // Act
        var page = await _fixture.AddPageAsync("page.md", content);
        await ReloadAsync(page, content, times: 3);
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.Contains(
            System.Net.WebUtility.HtmlEncode("'high' is not a valid value for 'Level'. Expected a value of type Int32."),
            MarkdownPageFixture.Html(page.Children));
        Assert.False(page.Children.ContainsKey("hosted"));
        Assert.Empty(Log.Running);
    }

    [Fact]
    public async Task WhenValueInTheJsonBlockOfReusedBlockDoesNotFit_ThenItsInstanceIsStopped()
    {
        // Arrange
        var content = $":: {HostedType}#hosted Name=A\n```json\n{{ \"Level\": 1 }}\n```\n::";
        var page = await _fixture.AddPageAsync("page.md", content);
        await _fixture.SettleHostedServicesAsync();
        var runningBefore = Log.Running;

        // Act
        await _fixture.UpdatePageAsync(page, content.Replace("\"Level\": 1", "\"Level\": \"high\""));
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.Single(runningBefore);
        Assert.Contains("is not a valid value for", MarkdownPageFixture.Html(page.Children));
        Assert.False(page.Children.ContainsKey("hosted"));
        Assert.Empty(Log.Running);
        Assert.Equal(1, Log.StartCount);
    }

    [Fact]
    public async Task WhenReleasingSubjectOfFailedBlockFails_ThenThePageStillRendersAndTheFailureIsLogged()
    {
        // Arrange
        var content =
            $":: {Namespace}.TestFailingDetach#failing Level=99\n::\n\n:: {HostedType}#hosted Name=A\n::";

        // Act
        var page = await _fixture.AddPageAsync("page.md", content);
        await _fixture.SettleHostedServicesAsync();

        // Assert
        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("TestFailingDetach&#39; could not be created or updated", html);
        Assert.DoesNotContain("detach failed", html);
        Assert.Same(page.Children["hosted"], Assert.Single(Log.Running));

        var warning = Assert.Single(_logger.Entries, entry => entry.Exception?.Message == "detach failed");
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("TestFailingDetach", warning.Message);
        Assert.Contains("page.md", warning.Message);
    }

    [Fact]
    public async Task WhenReleasingSubjectOfCancelledReloadFails_ThenTheCancellationIsReportedAndTheOthersAreReleased()
    {
        // Arrange
        var page = await _fixture.AddPageAsync("page.md", $":: {Namespace}.TestFaulty#faulty Mode=ok\n::");
        using var cancellationSource = new CancellationTokenSource();
        Assert.IsType<TestFaulty>(page.Children["faulty"]).CancellationSource = cancellationSource;

        // Act
        var exception = await Record.ExceptionAsync(() => _fixture.UpdatePageAsync(
            page,
            $":: {Namespace}.TestFailingDetach#failing Level=1\n::\n\n:: {HostedType}#added Name=B\n::\n\n" +
            $":: {Namespace}.TestFaulty#faulty Mode={TestFaulty.CancelMode}\n::",
            cancellationSource.Token));
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Empty(Log.Running);
        Assert.Single(_logger.Entries, entry => entry.Exception?.Message == "detach failed");
    }

    [Fact]
    public async Task WhenSlotBlockHasUnknownNameAfterValidSlotValue_ThenNothingKeepsRunning()
    {
        // Arrange
        var content = $$"""
            :: {{Namespace}}.TestPanel#panel
            ::: Row Label=A
            :::: {{HostedType}}#inner
            ::::
            :::
            ::: Nope
            :::
            ::
            """;

        // Act
        var page = await _fixture.AddPageAsync("page.md", content);
        await ReloadAsync(page, content, times: 3);
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.Contains("has no slot", MarkdownPageFixture.Html(page.Children));
        Assert.Empty(Log.Running);
        Assert.Equal(0, Log.StartCount);
    }

    [Fact]
    public async Task WhenLaterSlotValueCannotBeCreated_ThenSubjectsInEarlierSlotValuesAreStopped()
    {
        // Arrange
        var content = $$"""
            :: {{Namespace}}.TestPanel#panel
            ::: Row Label=A
            :::: {{HostedType}}#inner
            ::::
            :::
            ::: Row Unknown=1
            :::
            ::
            """;

        // Act
        var page = await _fixture.AddPageAsync("page.md", content);
        await ReloadAsync(page, content, times: 3);
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.Contains("has no configuration property", MarkdownPageFixture.Html(page.Children));
        Assert.Empty(Log.Running);
    }

    [Fact]
    public async Task WhenLaterSlotValueOfReusedWidgetFails_ThenSubjectsThatWerePutIntoItAreStopped()
    {
        // Arrange
        var page = await _fixture.AddPageAsync("page.md", $":: {Namespace}.TestPanel#panel\n::");
        var broken = $$"""
            :: {{Namespace}}.TestPanel#panel
            ::: Item
            :::: {{HostedType}}#inner
            ::::
            :::
            ::: Row Unknown=1
            :::
            ::
            """;

        // Act
        await _fixture.UpdatePageAsync(page, broken);
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.Contains("has no configuration property", MarkdownPageFixture.Html(page.Children));
        Assert.Empty(Log.Running);
    }

    [Fact]
    public async Task WhenReloadIsCancelledAfterBlockWasCreated_ThenCreatedInstanceIsStoppedAndPublishedOnesKeepRunning()
    {
        // Arrange
        var page = await _fixture.AddPageAsync(
            "page.md",
            $":: {HostedType}#kept Name=A\n::\n\n:: {Namespace}.TestFaulty#faulty Mode=ok\n::");
        await _fixture.SettleHostedServicesAsync();
        var kept = page.Children["kept"];
        using var cancellationSource = new CancellationTokenSource();
        Assert.IsType<TestFaulty>(page.Children["faulty"]).CancellationSource = cancellationSource;

        // Act
        var exception = await Record.ExceptionAsync(() => _fixture.UpdatePageAsync(
            page,
            $":: {HostedType}#kept Name=A\n::\n\n:: {HostedType}#added Name=B\n::\n\n" +
            $":: {Namespace}.TestFaulty#faulty Mode={TestFaulty.CancelMode}\n::",
            cancellationSource.Token));
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.False(page.Children.ContainsKey("added"));
        Assert.Same(kept, Assert.Single(Log.Running));
    }

    [Fact]
    public async Task WhenReloadIsCancelledInsideNewWidget_ThenSubjectsNestedInItAreStopped()
    {
        // Arrange
        var page = await _fixture.AddPageAsync(
            "page.md",
            $":: {HostedType}#kept Name=A\n::\n\n:: {Namespace}.TestFaulty#faulty Mode=ok\n::");
        await _fixture.SettleHostedServicesAsync();
        var kept = page.Children["kept"];
        using var cancellationSource = new CancellationTokenSource();
        Assert.IsType<TestFaulty>(page.Children["faulty"]).CancellationSource = cancellationSource;

        // Act
        var exception = await Record.ExceptionAsync(() => _fixture.UpdatePageAsync(
            page,
            $$"""
            :: {{HostedType}}#kept Name=A
            ::

            :: {{Namespace}}.TestPanel#panel
            ::: Row Label=A
            :::: {{HostedType}}#inRow
            ::::
            :::

            ::: {{HostedType}}#inContent
            :::
            ::

            :: {{Namespace}}.TestFaulty#faulty Mode={{TestFaulty.CancelMode}}
            ::
            """,
            cancellationSource.Token));
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Same(kept, Assert.Single(Log.Running));
    }

    [Fact]
    public async Task WhenReloadIsCancelledAfterSubjectWasPutIntoReusedWidget_ThenItRunsUntilItLeavesThePage()
    {
        // Arrange
        var before = $":: {Namespace}.TestPanel#panel\n::\n\n:: {Namespace}.TestFaulty#faulty Mode=ok\n::";
        var page = await _fixture.AddPageAsync("page.md", before);
        var panel = Assert.IsType<TestPanel>(page.Children["panel"]);
        using var cancellationSource = new CancellationTokenSource();
        Assert.IsType<TestFaulty>(page.Children["faulty"]).CancellationSource = cancellationSource;

        // Act
        var exception = await Record.ExceptionAsync(() => _fixture.UpdatePageAsync(
            page,
            $$"""
            :: {{Namespace}}.TestPanel#panel
            ::: {{HostedType}}#inner
            :::
            ::

            :: {{Namespace}}.TestFaulty#faulty Mode={{TestFaulty.CancelMode}}
            ::
            """,
            cancellationSource.Token));
        await _fixture.SettleHostedServicesAsync();
        var runningAfterCancellation = Log.Running;
        var content = Assert.IsType<MarkdownFragment>(panel.Content);

        await _fixture.UpdatePageAsync(page, before);
        await _fixture.SettleHostedServicesAsync();

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Same(panel, page.Children["panel"]);
        Assert.Same(content.Children["inner"], Assert.Single(runningAfterCancellation));
        Assert.Empty(Log.Running);
        Assert.Equal(1, Log.StartCount);
    }
}
