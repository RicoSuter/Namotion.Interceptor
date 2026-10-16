using System.Runtime.Versioning;
using HomeBlaze.Abstractions;
using System.Text.RegularExpressions;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Cli;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the chat of the agent page, with a shell script standing in for the Claude CLI.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class AgentPageTests
{
    private const string AgentPath = "Agents/TestAgent";
    private const string TimelineAgentPath = "Agents/TimelineAgent";
    private const string ScrollAgentPath = "Agents/ScrollAgent";
    private const string TypingAgentPath = "Agents/TypingAgent";
    private const string ResultAgentPath = "Agents/ResultAgent";
    private const int PageLoadTimeout = 30000;
    private const int ReplyTimeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public AgentPageTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenSendingAChatMessage_ThenTheReplyOfTheCliIsShown()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The fake CLI is a shell script.
        }

        // Arrange - the reply is unique per run, so a timeline left behind by an earlier run cannot satisfy the assertion.
        var reply = "Hello from the fake CLI " + Guid.NewGuid().ToString("N");
        var output = InitLine + "\n" +
            $$$"""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{reply}}}"}},"session_id":"e2e-session"}""" + "\n" +
            ResultLine(reply);

        // Act & Assert
        await RunChatAsync(output, async page =>
            await Assertions.Expect(page.GetByTestId("agent-reply").Last).ToContainTextAsync(reply, new() { Timeout = ReplyTimeout }));
    }

    [Fact]
    public async Task WhenTheAnswerHasTextAroundAToolCall_ThenNoTextDisappearsWhenTheReplyLands()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The fake CLI is a shell script.
        }

        // Arrange - the CLI result holds only the text after the tool call; the script waits for a signal file before
        // writing it, so the test sees the streamed answer first and then the stored reply.
        var before = "Checking the sensors " + Guid.NewGuid().ToString("N");
        var after = "Both logged " + Guid.NewGuid().ToString("N");

        static string StreamEvent(string streamEvent) =>
            $$$"""{"type":"stream_event","event":{{{streamEvent}}},"session_id":"e2e-session"}""";

        string CreateScript(string directory) =>
            "#!/bin/sh\ncat > /dev/null\ncat <<'EOF'\n" +
            InitLine + "\n" +
            StreamEvent("""{"type":"message_start","message":{"id":"msg_1","role":"assistant","content":[]}}""") + "\n" +
            StreamEvent("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""") + "\n" +
            StreamEvent($$$"""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{before}}}"}}""") + "\n" +
            StreamEvent("""{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"mcp__homeblaze__get_property","input":{}}}""") + "\n" +
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"21.5"}]},"session_id":"e2e-session"}""" + "\n" +
            StreamEvent("""{"type":"message_start","message":{"id":"msg_2","role":"assistant","content":[]}}""") + "\n" +
            StreamEvent("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""") + "\n" +
            StreamEvent($$$"""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{after}}}"}}""") + "\n" +
            "EOF\n" +
            $"while [ -d '{directory}' ] && [ ! -f '{directory}/finish' ]; do sleep 0.05; done\n" +
            "cat <<'EOF'\n" + ResultLine(after) + "\nEOF\n";

        // Act & Assert
        await RunChatAsync(CreateScript, async (page, directory) =>
        {
            var streaming = page.GetByTestId("agent-streaming");
            await Assertions.Expect(streaming).ToContainTextAsync(before, new() { Timeout = ReplyTimeout });
            await Assertions.Expect(streaming).ToContainTextAsync(after, new() { Timeout = ReplyTimeout });

            Signal(directory, "finish");
            await Assertions.Expect(streaming).ToHaveCountAsync(0, new() { Timeout = ReplyTimeout });
            var reply = page.GetByTestId("agent-reply").Last;
            await Assertions.Expect(reply).ToContainTextAsync(before, new() { Timeout = ReplyTimeout });
            await Assertions.Expect(reply).ToContainTextAsync(after, new() { Timeout = ReplyTimeout });
        });
    }

    [Fact]
    public async Task WhenAChatTurnRan_ThenTheActivityTabShowsTheLastActivityInTilesThatFitTheWidth()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The fake CLI is a shell script.
        }

        // Arrange - the rate limit line adds the two usage tiles, so the tab shows all five.
        var output = InitLine + "\n" + """
            {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}},"session_id":"e2e-session"}
            {"type":"rate_limit_event","rate_limit_info":{"status":"allowed","unifiedWindows":{"five_hour":{"utilization":0.18,"resetsAt":1791663000},"seven_day":{"utilization":0.73,"resetsAt":1791748800}}}}
            """ + "\n" + ResultLine("Hello");

        // Act & Assert
        await RunChatAsync(output, async page =>
        {
            await Assertions.Expect(page.GetByTestId("agent-reply").Last).ToContainTextAsync("Hello", new() { Timeout = ReplyTimeout });
            await page.GetByRole(AriaRole.Tab, new() { Name = "Activity" }).ClickAsync();
            var stats = page.GetByTestId("agent-stats");
            await Assertions.Expect(stats.Locator(":scope > *")).ToHaveCountAsync(5, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(stats).ToContainTextAsync("Last activity");
            await Assertions.Expect(stats).ToContainTextAsync("Last background run");
            Assert.DoesNotContain("never", await stats.Locator(":scope > *").Nth(2).InnerTextAsync());

            foreach (var (width, expectedRows) in new[] { (1440, 1), (390, 3) })
            {
                await page.SetViewportSizeAsync(width, 900);
                await page.WaitForFunctionAsync(
                    "expected => new Set([...document.querySelector('[data-testid=agent-stats]').children].map(tile => Math.round(tile.getBoundingClientRect().top))).size === expected",
                    expectedRows);
                var overflows = await stats.EvaluateAsync<bool>(
                    "grid => grid.scrollWidth > grid.clientWidth || [...grid.children].some(tile => tile.scrollWidth > tile.clientWidth)");
                Assert.False(overflows, $"The stat tiles overflow at a width of {width} px.");
            }
        });
    }

    [Fact]
    public async Task WhenTheCliIsNotLoggedIn_ThenTheChatShowsTheLoginError()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The fake CLI is a shell script.
        }

        // Arrange
        const string output = InitLine + "\n" + """
            {"type":"assistant","message":{"model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"Not logged in · Please run /login"}]},"error":"authentication_failed","session_id":"e2e-session"}
            {"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login","session_id":"e2e-session","usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0}}
            """;

        // Act & Assert
        await RunChatAsync(output, async page =>
        {
            await Assertions.Expect(page.GetByTestId("agent-error").Last).ToContainTextAsync("not logged in", new() { Timeout = ReplyTimeout });
            await Assertions.Expect(page.GetByTestId("agent-status")).ToContainTextAsync("not logged in", new() { Timeout = ReplyTimeout });
        });
    }

    [Fact]
    public async Task WhenTheReaderStaysAtTheBottomOrScrollsUpWhileStreaming_ThenOnlyTheReaderAtTheBottomIsFollowed()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The fake CLI is a shell script.
        }

        // Arrange - the script streams enough paragraphs to overflow the timeline, then waits for a signal file before
        // each further batch, so the test controls when content grows. Deleting the directory ends the waits.
        static string Delta(string text) =>
            $$$"""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{text}}}"}},"session_id":"e2e-session"}""";

        static string Paragraphs(string name, int count) =>
            string.Concat(Enumerable.Range(1, count).Select(index => $"{name} {index}\\n\\n"));

        static string WaitFor(string directory, string signal) =>
            $"while [ -d '{directory}' ] && [ ! -f '{directory}/{signal}' ]; do sleep 0.05; done\n";

        string CreateScript(string directory) =>
            "#!/bin/sh\ncat > /dev/null\n" +
            "cat <<'EOF'\n" +
            InitLine + "\n" +
            Delta(Paragraphs("line", 40)) + "\nEOF\n" +
            WaitFor(directory, "small") + "cat <<'EOF'\n" + Delta(Paragraphs("small", 10)) + "\nEOF\n" +
            WaitFor(directory, "far") + "cat <<'EOF'\n" + Delta(Paragraphs("far", 10)) + "\nEOF\n" +
            WaitFor(directory, "back") + "cat <<'EOF'\n" + Delta(Paragraphs("back", 10)) + "\nEOF\n" +
            WaitFor(directory, "finish") +
            "cat <<'EOF'\n" + Delta("done") + "\n" + ResultLine(Paragraphs("line", 40) + "done") + "\nEOF\n";

        // Act & Assert
        await RunChatAsync(CreateScript, async (page, directory) =>
        {
            var streaming = page.GetByTestId("agent-streaming");
            var timelineLocator = page.GetByTestId("agent-timeline");
            var timeline = await timelineLocator.ElementHandleAsync();

            // At the bottom, the streamed paragraphs keep the timeline scrolled to the newest one.
            await Assertions.Expect(streaming).ToContainTextAsync("line 40", new() { Timeout = ReplyTimeout });
            await WaitForDistanceToBottomAsync(page, timeline, "<= 32", "timeline.scrollHeight > timeline.clientHeight + 200");

            // Even a small scroll up releases the view, so further paragraphs leave it where the reader put it.
            await timelineLocator.HoverAsync();
            await page.Mouse.WheelAsync(0, -10);
            await WaitForDistanceToBottomAsync(page, timeline, ">= 9");
            await AssertContentStaysAsync(page, timeline, () => Signal(directory, "small"), streaming, "small 10");

            // So does a far scroll up.
            await page.Mouse.WheelAsync(0, -300);
            await WaitForDistanceToBottomAsync(page, timeline, "> 200");
            await AssertContentStaysAsync(page, timeline, () => Signal(directory, "far"), streaming, "far 10");

            // Back at the bottom, the timeline follows again.
            await page.Mouse.WheelAsync(0, 100000);
            await WaitForDistanceToBottomAsync(page, timeline, "<= 1");
            Signal(directory, "back");
            await Assertions.Expect(streaming).ToContainTextAsync("back 10", new() { Timeout = ReplyTimeout });
            await WaitForDistanceToBottomAsync(page, timeline, "<= 32");

            Signal(directory, "finish");
            await Assertions.Expect(page.GetByTestId("agent-reply").Last).ToContainTextAsync("done", new() { Timeout = ReplyTimeout });

            // Sending scrolls a reader who scrolled up back to the bottom; the reply is long enough to scroll, and the
            // script runs through on the second turn because its signals exist. The count is relative, because the
            // shared test agent keeps the replies of other tests in its timeline.
            var replies = page.GetByTestId("agent-reply");
            var replyCount = await replies.CountAsync();
            await page.Mouse.WheelAsync(0, -300);
            await WaitForDistanceToBottomAsync(page, timeline, "> 200");
            await page.GetByTestId("agent-chat-input").Locator("textarea").FillAsync("Again?");
            await page.GetByTestId("agent-chat-send").ClickAsync();
            await Assertions.Expect(replies).ToHaveCountAsync(replyCount + 1, new() { Timeout = ReplyTimeout });
            await WaitForDistanceToBottomAsync(page, timeline, "<= 32");
        });
    }

    [Fact]
    public async Task WhenTheTimelineIsLong_ThenTheChatShowsTheNewestRowsAndTheActivityTabPages()
    {
        // Arrange - a separate agent, so the seeded entries never reach the chat tests.
        var (page, resolver, agent) = await OpenSeededAgentPageAsync(TimelineAgentPath);
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        try
        {
            // 60 questions alternating with 60 background runs, so every entry is its own row.
            for (var index = 0; index < 120; index++)
            {
                await agent.Timeline.AppendAsync(index % 2 == 0
                    ? new AgentTimelineEntry { Kind = AgentTimelineEntryKind.User, Timestamp = start.AddMinutes(index), Text = $"Question {index}" }
                    : new AgentTimelineEntry { Kind = AgentTimelineEntryKind.BackgroundRun, Timestamp = start.AddMinutes(index), Text = $"Run {index}", Trigger = AgentTriggers.Poll },
                    CancellationToken.None);
            }

            await page.ReloadAsync();
            var timeline = page.GetByTestId("agent-timeline");
            var rows = page.GetByTestId("agent-timeline-row");

            // Act & Assert - the chat starts with the newest 50 rows.
            await Assertions.Expect(timeline).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(rows).ToHaveCountAsync(50, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(rows.First).ToHaveTextAsync("Question 70");

            // Show earlier adds 50 rows above and keeps the row that was first where it was.
            await timeline.HoverAsync();
            await page.Mouse.WheelAsync(0, -100000);
            await WaitForTopAsync(timeline);
            var heldRow = rows.Filter(new() { HasText = "Question 70" });
            var offsetBefore = await ReadOffsetInTimelineAsync(heldRow);
            await page.GetByTestId("agent-timeline-show-earlier").ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(100, new() { Timeout = PageLoadTimeout });
            Assert.Equal(offsetBefore, await ReadOffsetInTimelineAsync(heldRow), 2.0);

            await page.Mouse.WheelAsync(0, -100000);
            await WaitForTopAsync(timeline);
            await page.GetByTestId("agent-timeline-show-earlier").ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(120, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(page.GetByTestId("agent-timeline-show-earlier")).ToHaveCountAsync(0);

            // The Activity tab pages the 60 runs, newest first.
            await page.GetByRole(AriaRole.Tab, new() { Name = "Activity" }).ClickAsync();
            var runRows = page.GetByTestId("agent-runs").Locator("tbody tr");
            await Assertions.Expect(runRows).ToHaveCountAsync(25, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(runRows.First).ToContainTextAsync("Run 119");
            var nextPage = page.GetByTestId("agent-runs-pager").GetByRole(AriaRole.Button, new() { Name = "Next page" });
            await nextPage.ClickAsync();
            await Assertions.Expect(runRows.First).ToContainTextAsync("Run 69");
            await nextPage.ClickAsync();
            await Assertions.Expect(runRows).ToHaveCountAsync(10);
            await Assertions.Expect(runRows.Last).ToContainTextAsync("Run 1");
        }
        finally
        {
            DeleteAgentData(resolver, agent);
        }
    }

    [Fact]
    public async Task WhenABackgroundRunHasTextBeforeItsResult_ThenTheCardAndTheActivityTabShowTheResult()
    {
        // Arrange - a separate agent, so the seeded entry never reaches the chat tests.
        var (page, resolver, agent) = await OpenSeededAgentPageAsync(ResultAgentPath);
        var status = page.GetByTestId("agent-status");
        var result = "Conveyor at **700 RPM** " + Guid.NewGuid().ToString("N");
        var preamble = "Checking the conveyor " + Guid.NewGuid().ToString("N");
        try
        {
            await agent.Timeline.AppendAsync(new AgentTimelineEntry
            {
                Kind = AgentTimelineEntryKind.BackgroundRun,
                Timestamp = DateTimeOffset.UtcNow,
                Text = preamble + "\n\n" + result,
                FinalText = result,
                Trigger = AgentTriggers.Poll
            }, CancellationToken.None);

            // Act
            await page.ReloadAsync();

            // Assert - one status chip, the card shows the result and the whole answer on request.
            await Assertions.Expect(status.Locator(".mud-chip")).ToHaveCountAsync(1, new() { Timeout = PageLoadTimeout });
            if (agent.Status == ServiceStatus.Running)
            {
                await Assertions.Expect(status).ToHaveTextAsync("Idle");
            }

            var card = page.GetByTestId("agent-background-run").Last;
            var plainResult = result.Replace("**", string.Empty);
            await Assertions.Expect(card).ToContainTextAsync(plainResult, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(card).Not.ToContainTextAsync(preamble);
            await card.GetByTestId("agent-show-full-answer").ClickAsync();
            await Assertions.Expect(card).ToContainTextAsync(preamble);

            await page.GetByRole(AriaRole.Tab, new() { Name = "Activity" }).ClickAsync();
            await Assertions.Expect(page.GetByTestId("agent-run-result").First).ToHaveTextAsync(plainResult, new() { Timeout = PageLoadTimeout });
        }
        finally
        {
            DeleteAgentData(resolver, agent);
        }
    }

    [Fact]
    public async Task WhenEditingACliAgent_ThenTheWebAndMarkdownToolsAreOfferedWithTheirWarnings()
    {
        // Act
        var dialog = await OpenEditDialogAsync(ResultAgentPath);

        // Assert
        var webSearch = dialog.GetByTestId("agent-edit-tool-web_search");
        await Assertions.Expect(webSearch).ToContainTextAsync("Searches the web.", new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(webSearch.GetByTestId("agent-edit-tool-web-warning")).ToHaveTextAsync("Loads untrusted web content");
        await Assertions.Expect(dialog.GetByTestId("agent-edit-tool-web_fetch")).ToContainTextAsync("Loads a web page.");
        await Assertions.Expect(dialog.GetByTestId("agent-edit-tool-browse").GetByTestId("agent-edit-tool-web-warning")).ToHaveCountAsync(0);

        var editMarkdown = dialog.GetByTestId("agent-edit-tool-edit_markdown");
        await Assertions.Expect(editMarkdown).ToContainTextAsync("Replaces a passage of a Markdown page or document.", new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(editMarkdown.GetByTestId("agent-edit-tool-pages-warning")).ToHaveTextAsync("Changes pages and documents");
        await Assertions.Expect(dialog.GetByTestId("agent-edit-tool-write_markdown").GetByTestId("agent-edit-tool-pages-warning")).ToHaveCountAsync(1);
        await Assertions.Expect(dialog.GetByTestId("agent-edit-tool-create_markdown").GetByTestId("agent-edit-tool-pages-warning")).ToHaveCountAsync(1);
        await Assertions.Expect(dialog.GetByTestId("agent-edit-tool-invoke_operation").GetByTestId("agent-edit-tool-pages-warning")).ToHaveCountAsync(0);
        await Assertions.Expect(dialog.GetByTestId("agent-edit-tool-invoke_operation")).ToContainTextAsync("Can change devices and send notifications");
    }

    [Fact]
    public async Task WhenSendingWithEnterAfterScrollingUpInALongTimeline_ThenTheMessageAndReplyShowAtTheBottom()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The fake CLI is a shell script.
        }

        // Arrange - a separate agent with more rows than the chat renders, so each new row also drops the oldest one
        // above the view.
        var (page, resolver, agent) = await OpenSeededAgentPageAsync(ScrollAgentPath);
        var reply = "Reply " + Guid.NewGuid().ToString("N");
        var message = "Message " + Guid.NewGuid().ToString("N");
        var scriptDirectory = Directory.CreateTempSubdirectory("e2e-fake-claude").FullName;
        var originalCliPath = agent.CliPath;
        try
        {
            var start = DateTimeOffset.UtcNow.AddDays(-1);
            for (var index = 0; index < 120; index++)
            {
                await agent.Timeline.AppendAsync(new AgentTimelineEntry
                {
                    Kind = index % 2 == 0 ? AgentTimelineEntryKind.User : AgentTimelineEntryKind.Assistant,
                    Timestamp = start.AddMinutes(index),
                    Text = $"Entry {index}"
                }, CancellationToken.None);
            }

            agent.CliPath = WriteFakeCli(
                scriptDirectory, "#!/bin/sh\ncat > /dev/null\ncat <<'EOF'\n" + InitLine + "\n" + ResultLine(reply) + "\nEOF\n");

            await page.ReloadAsync();
            var timelineLocator = page.GetByTestId("agent-timeline");
            await Assertions.Expect(timelineLocator).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
            var timeline = await timelineLocator.ElementHandleAsync();
            await timelineLocator.HoverAsync();
            await page.Mouse.WheelAsync(0, -600);
            await WaitForDistanceToBottomAsync(page, timeline, "> 500");

            // Act
            var input = page.GetByTestId("agent-chat-input").Locator("textarea");
            await input.FillAsync(message);
            await input.PressAsync("Enter");

            // Assert - the message shows at the bottom right away, and the reply follows it there.
            await Assertions.Expect(page.GetByTestId("agent-timeline-row").Filter(new() { HasText = message })).ToBeVisibleAsync(new() { Timeout = ReplyTimeout });
            await WaitForDistanceToBottomAsync(page, timeline, "<= 32");
            await Assertions.Expect(page.GetByTestId("agent-reply").Last).ToContainTextAsync(reply, new() { Timeout = ReplyTimeout });
            await WaitForDistanceToBottomAsync(page, timeline, "<= 32");
            Assert.True(await IsInViewAsync(page.GetByTestId("agent-timeline-row").Filter(new() { HasText = message })));
        }
        finally
        {
            agent.CliPath = originalCliPath;
            DeleteAgentData(resolver, agent);
            Directory.Delete(scriptDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WhenAddingAndDeletingAMemoryEntry_ThenTheMemoryTableShowsEachChange()
    {
        // Arrange - the key is unique per run, so an entry left behind by an earlier run cannot satisfy the assertion.
        var key = "e2e/" + Guid.NewGuid().ToString("N");
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/{AgentPath}");
        await Assertions.Expect(page.GetByTestId("agent-chat-input")).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });

        // The prerendered tabs are visible before the circuit connects, and a click then never reaches the component;
        // the timeline stays hidden until the first interactive render.
        await Assertions.Expect(page.GetByTestId("agent-timeline")).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
        var agent = Assert.IsType<ClaudeCliAgent>(resolver.ResolveSubject("/" + AgentPath, PathStyle.Canonical));
        var table = page.GetByTestId("agent-memory");

        try
        {
            // Act & Assert
            await page.GetByRole(AriaRole.Tab, new() { Name = "Memory" }).ClickAsync();
            await page.GetByTestId("agent-memory-add").ClickAsync();
            await page.GetByTestId("agent-memory-key").Locator("input").FillAsync(key);
            await page.GetByTestId("agent-memory-value").Locator("textarea").FillAsync("noted by the end-to-end test");
            await page.GetByTestId("agent-memory-save").ClickAsync();
            await Assertions.Expect(table).ToContainTextAsync(key, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(table).ToContainTextAsync("noted by the end-to-end test");
            Assert.Equal("noted by the end-to-end test", agent.Memory.GetSnapshot().TryGetEntry(key)?.Value);

            await page.GetByRole(AriaRole.Button, new() { Name = "Delete " + key }).ClickAsync();
            await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
            await Assertions.Expect(table).Not.ToContainTextAsync(key, new() { Timeout = PageLoadTimeout });
            Assert.Null(agent.Memory.GetSnapshot().TryGetEntry(key));
        }
        finally
        {
            DeleteAgentData(resolver, agent);
        }
    }

    private const string InitLine = """{"type":"system","subtype":"init","session_id":"e2e-session","tools":[],"mcp_servers":[]}""";

    private static string ResultLine(string result) =>
        $$$"""{"type":"result","subtype":"success","is_error":false,"result":"{{{result}}}","session_id":"e2e-session","usage":{"input_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}""";

    [UnsupportedOSPlatform("windows")]
    private static string WriteFakeCli(string directory, string script)
    {
        var scriptPath = Path.Combine(directory, "claude");
        ExecutableScripts.Write(scriptPath, script);
        return scriptPath;
    }

    [Fact]
    public async Task WhenTypingIntoTheChatInput_ThenTheTimelineStaysStill()
    {
        // Arrange - a separate agent whose timeline is long enough to scroll; nothing is sent.
        var (page, resolver, agent) = await OpenSeededAgentPageAsync(TypingAgentPath);
        try
        {
            var start = DateTimeOffset.UtcNow.AddDays(-1);
            for (var index = 0; index < 60; index++)
            {
                await agent.Timeline.AppendAsync(new AgentTimelineEntry
                {
                    Kind = index % 2 == 0 ? AgentTimelineEntryKind.User : AgentTimelineEntryKind.Assistant,
                    Timestamp = start.AddMinutes(index),
                    Text = $"Entry {index}"
                }, CancellationToken.None);
            }

            await page.ReloadAsync();
            var timelineLocator = page.GetByTestId("agent-timeline");
            await Assertions.Expect(timelineLocator).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
            var timeline = await timelineLocator.ElementHandleAsync();
            var input = page.GetByTestId("agent-chat-input").Locator("textarea");

            // Act & Assert - at the bottom, then scrolled up, the rows keep their place on every keystroke and the
            // timeline does not scroll at all, not even for a moment that a later frame corrects.
            await AssertTypingKeepsTheTimelineStillAsync(page, timeline, input, "Pinned to the bottom ");

            await timelineLocator.HoverAsync();
            await page.Mouse.WheelAsync(0, -400);
            await WaitForDistanceToBottomAsync(page, timeline, "> 300");
            await AssertTypingKeepsTheTimelineStillAsync(page, timeline, input, "and scrolled up too.");
        }
        finally
        {
            DeleteAgentData(resolver, agent);
        }
    }

    private static async Task AssertTypingKeepsTheTimelineStillAsync(IPage page, IElementHandle timeline, ILocator input, string text)
    {
        // A wheel scroll or the pin to the bottom after the load can still be animating; only scrolls caused by typing
        // may be counted.
        await WaitForScrollToSettleAsync(page, timeline);

        // The row in the middle of the view is followed; scroll events are counted while typing.
        await timeline.EvaluateAsync(
            "timeline => { const view = timeline.getBoundingClientRect(); " +
            "window.__typingRow = [...timeline.querySelectorAll('[data-testid=agent-timeline-row]')].find(row => row.getBoundingClientRect().bottom > view.top + view.height / 2); " +
            "window.__typingScrolls = 0; window.__typingListener = () => window.__typingScrolls++; timeline.addEventListener('scroll', window.__typingListener); }");
        await input.FocusAsync();
        var offset = await ReadTypingRowOffsetAsync(page, timeline);
        foreach (var character in text)
        {
            await page.Keyboard.TypeAsync(character.ToString());
            Assert.Equal(offset, await ReadTypingRowOffsetAsync(page, timeline), 1.0);
        }

        var scrolls = await timeline.EvaluateAsync<int>("timeline => { timeline.removeEventListener('scroll', window.__typingListener); return window.__typingScrolls; }");
        Assert.Equal(0, scrolls);
    }

    // Resolves once the scroll position has not changed for ten animation frames in a row, and fails when it keeps
    // changing for 300 frames, so a timeline that never stops scrolling fails the test instead of hanging it.
    private static Task WaitForScrollToSettleAsync(IPage page, IElementHandle timeline) =>
        page.EvaluateAsync(
            "timeline => new Promise((resolve, reject) => { let last = timeline.scrollTop, stableFrames = 0, frames = 0; " +
            "const check = () => { frames++; if (timeline.scrollTop === last) { stableFrames++; } else { last = timeline.scrollTop; stableFrames = 0; } " +
            "if (stableFrames >= 10) { resolve(); } else if (frames >= 300) { reject(new Error('The timeline kept scrolling.')); } else { requestAnimationFrame(check); } }; " +
            "requestAnimationFrame(check); })",
            timeline);

    // Read after two animation frames, so the browser has laid out and painted what the keystroke changed.
    private static Task<double> ReadTypingRowOffsetAsync(IPage page, IElementHandle timeline) =>
        page.EvaluateAsync<double>(
            "timeline => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(" +
            "window.__typingRow.getBoundingClientRect().top - timeline.getBoundingClientRect().top))))",
            timeline);

    /// <summary>
    /// Opens the page of an agent that a test seeds with timeline entries, once the agent has loaded its storage, which
    /// would replace entries appended before.
    /// </summary>
    private async Task<(IPage Page, SubjectPathResolver Resolver, ClaudeCliAgent Agent)> OpenSeededAgentPageAsync(string agentPath)
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/{agentPath}");
        await Assertions.Expect(page.GetByTestId("agent-status")).ToContainTextAsync(
            new Regex("Idle|Unavailable|Error"), new() { Timeout = PageLoadTimeout });

        var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
        var agent = Assert.IsType<ClaudeCliAgent>(resolver.ResolveSubject("/" + agentPath, PathStyle.Canonical));
        return (page, resolver, agent);
    }

    private async Task<ILocator> OpenEditDialogAsync(string path)
    {
        var page = await SubjectBrowserPage.OpenAsync(_fixture, path);
        var editButton = page.Locator("#scrollContainer [data-testid='edit-subject-button']").Last;
        await Assertions.Expect(editButton).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await editButton.ClickAsync();
        return page.GetByRole(AriaRole.Dialog);
    }

    private static Task<bool> IsInViewAsync(ILocator row) =>
        row.EvaluateAsync<bool>(
            "row => { const view = row.closest('[data-testid=agent-timeline]').getBoundingClientRect(); " +
            "const box = row.getBoundingClientRect(); return box.top >= view.top - 1 && box.bottom <= view.bottom + 1; }");

    private static async Task WaitForTopAsync(ILocator timeline)
    {
        var handle = await timeline.ElementHandleAsync();
        await timeline.Page.WaitForFunctionAsync("timeline => timeline.scrollTop === 0", handle, new() { Timeout = ReplyTimeout });
    }

    // Read after two animation frames, so the script that keeps the row in place has run.
    private static Task<double> ReadOffsetInTimelineAsync(ILocator row) =>
        row.EvaluateAsync<double>(
            "row => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(" +
            "row.getBoundingClientRect().top - row.closest('[data-testid=agent-timeline]').getBoundingClientRect().top))))");

    private static void Signal(string directory, string signal) =>
        File.WriteAllText(Path.Combine(directory, signal), string.Empty);

    private static async Task WaitForDistanceToBottomAsync(IPage page, IElementHandle timeline, string condition, string? alsoRequired = null)
    {
        try
        {
            await page.WaitForFunctionAsync(
                $"timeline => timeline.scrollHeight - timeline.scrollTop - timeline.clientHeight {condition}" +
                (alsoRequired is null ? string.Empty : " && " + alsoRequired),
                timeline, new() { Timeout = ReplyTimeout });
        }
        catch (TimeoutException exception)
        {
            var state = await timeline.EvaluateAsync<string>(
                "timeline => `scrollTop ${timeline.scrollTop}, scrollHeight ${timeline.scrollHeight}, clientHeight ${timeline.clientHeight}, visibility ${getComputedStyle(timeline).visibility}`");
            throw new TimeoutException($"The timeline's distance to the bottom never became {condition}: {state}", exception);
        }
    }

    private static async Task AssertContentStaysAsync(IPage page, IElementHandle timeline, Action growContent, ILocator streaming, string grownText)
    {
        var contentOffset = await ReadContentOffsetAfterLayoutAsync(page, timeline);
        growContent();
        await Assertions.Expect(streaming).ToContainTextAsync(grownText, new() { Timeout = ReplyTimeout });
        Assert.Equal(contentOffset, await ReadContentOffsetAfterLayoutAsync(page, timeline), 1.0);
    }

    // Where the content sits in the timeline's view, read after two animation frames so the resize observations
    // that scroll the timeline have been delivered.
    private static Task<double> ReadContentOffsetAfterLayoutAsync(IPage page, IElementHandle timeline) =>
        page.EvaluateAsync<double>(
            "timeline => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => " +
            "resolve(timeline.firstElementChild.getBoundingClientRect().top - timeline.getBoundingClientRect().top))))",
            timeline);

    /// <summary>
    /// Points the test agent at a script that replays <paramref name="output"/>, sends a chat message from its
    /// page and runs <paramref name="assert"/> on the page, then restores the agent and deletes its data files.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private Task RunChatAsync(string output, Func<IPage, Task> assert) =>
        RunChatAsync(
            _ => "#!/bin/sh\ncat > /dev/null\ncat <<'EOF'\n" + output.Replace("\r\n", "\n") + "\nEOF\n",
            (page, _) => assert(page));

    /// <summary>
    /// Points the test agent at the script <paramref name="createScript"/> writes for its directory, sends a chat
    /// message from its page and runs <paramref name="assert"/> on the page with that directory, then restores the
    /// agent and deletes its data files and the directory.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private async Task RunChatAsync(Func<string, string> createScript, Func<IPage, string, Task> assert)
    {
        var scriptDirectory = Directory.CreateTempSubdirectory("e2e-fake-claude").FullName;
        try
        {
            var scriptPath = WriteFakeCli(scriptDirectory, createScript(scriptDirectory));

            // The page shows the chat only once the storage has loaded the agent, which can still be under way
            // right after the host started, so the agent is resolved after that.
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{AgentPath}");
            var input = page.GetByTestId("agent-chat-input").Locator("textarea");
            await Assertions.Expect(input).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });

            // The prerendered input is visible before the circuit connects, and text filled in then never reaches the
            // component. The timeline stays hidden until the chat script ran after the first interactive render.
            await Assertions.Expect(page.GetByTestId("agent-timeline")).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });

            var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
            var agent = Assert.IsType<ClaudeCliAgent>(resolver.ResolveSubject("/" + AgentPath, PathStyle.Canonical));
            var originalCliPath = agent.CliPath;
            agent.CliPath = scriptPath;
            try
            {
                await input.FillAsync("Hello?");
                var send = page.GetByTestId("agent-chat-send");
                await Assertions.Expect(send).ToBeEnabledAsync(new() { Timeout = PageLoadTimeout });
                await send.ClickAsync();

                await assert(page, scriptDirectory);
            }
            finally
            {
                agent.CliPath = originalCliPath;
                DeleteAgentData(resolver, agent);
            }
        }
        finally
        {
            Directory.Delete(scriptDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Deletes the timeline, session, memory and working directory the agent keeps in the data directory of the
    /// host, which is the output folder of this project.
    /// </summary>
    private static void DeleteAgentData(SubjectPathResolver resolver, ClaudeCliAgent agent)
    {
        var agentDirectory = Path.Combine(AppContext.BaseDirectory, "Agents");
        var path = resolver.GetPath(agent, PathStyle.Canonical);
        if (path is null || !Directory.Exists(agentDirectory))
        {
            return;
        }

        var storageName = AgentStorage.GetName(path);
        foreach (var file in Directory.GetFiles(agentDirectory, storageName + ".*"))
        {
            File.Delete(file);
        }

        foreach (var directory in Directory.GetDirectories(agentDirectory, storageName + ".*"))
        {
            Directory.Delete(directory, recursive: true);
        }

        if (!Directory.EnumerateFileSystemEntries(agentDirectory).Any())
        {
            Directory.Delete(agentDirectory);
        }
    }
}
