using System.Runtime.Versioning;
using HomeBlaze.Abstractions;
using System.Text.RegularExpressions;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Cli;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

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
        var output = $$$"""
            {"type":"system","subtype":"init","session_id":"e2e-session","tools":[],"mcp_servers":[]}
            {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{reply}}}"}},"session_id":"e2e-session"}
            {"type":"result","subtype":"success","is_error":false,"result":"{{{reply}}}","session_id":"e2e-session","usage":{"input_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}
            """;

        // Act & Assert
        await RunChatAsync(output, async page =>
            await Assertions.Expect(page.GetByTestId("agent-reply").Last).ToContainTextAsync(reply, new() { Timeout = ReplyTimeout }));
    }

    [Fact]
    public async Task WhenTheCliIsNotLoggedIn_ThenTheChatShowsTheLoginError()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The fake CLI is a shell script.
        }

        // Arrange
        const string output = """
            {"type":"system","subtype":"init","session_id":"e2e-session","tools":[],"mcp_servers":[]}
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
            """{"type":"system","subtype":"init","session_id":"e2e-session","tools":[],"mcp_servers":[]}""" + "\n" +
            Delta(Paragraphs("line", 40)) + "\nEOF\n" +
            WaitFor(directory, "small") + "cat <<'EOF'\n" + Delta(Paragraphs("small", 10)) + "\nEOF\n" +
            WaitFor(directory, "far") + "cat <<'EOF'\n" + Delta(Paragraphs("far", 10)) + "\nEOF\n" +
            WaitFor(directory, "back") + "cat <<'EOF'\n" + Delta(Paragraphs("back", 10)) + "\nEOF\n" +
            WaitFor(directory, "finish") +
            "cat <<'EOF'\n" +
            $$$"""{"type":"result","subtype":"success","is_error":false,"result":"{{{Paragraphs("line", 40)}}}done","session_id":"e2e-session","usage":{"input_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}""" +
            "\nEOF\n";

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
            // script runs through on the second turn because its signals exist.
            await page.Mouse.WheelAsync(0, -300);
            await WaitForDistanceToBottomAsync(page, timeline, "> 200");
            await page.GetByTestId("agent-chat-input").Locator("textarea").FillAsync("Again?");
            await page.GetByTestId("agent-chat-send").ClickAsync();
            await Assertions.Expect(page.GetByTestId("agent-reply")).ToHaveCountAsync(2, new() { Timeout = ReplyTimeout });
            await WaitForDistanceToBottomAsync(page, timeline, "<= 32");
        });
    }

    [Fact]
    public async Task WhenTheTimelineIsLong_ThenTheChatShowsTheNewestRowsAndTheActivityTabPages()
    {
        // Arrange - a separate agent, so the seeded entries never reach the chat tests. Its storage is loaded once it
        // runs; entries appended before that would be replaced by the stored timeline.
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/{TimelineAgentPath}");
        await Assertions.Expect(page.GetByTestId("agent-status")).ToContainTextAsync(
            new Regex("Running|Unavailable|Error"), new() { Timeout = PageLoadTimeout });

        var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
        var agent = Assert.IsType<ClaudeCliAgent>(resolver.ResolveSubject("/" + TimelineAgentPath, PathStyle.Canonical));
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        try
        {
            // 60 questions alternating with 60 background runs, so every entry is its own row.
            for (var index = 0; index < 120; index++)
            {
                await agent.Timeline.AppendAsync(index % 2 == 0
                    ? new AgentTimelineEntry { Kind = AgentTimelineEntryKind.User, Timestamp = start.AddMinutes(index), Text = $"Question {index}" }
                    : new AgentTimelineEntry { Kind = AgentTimelineEntryKind.BackgroundRun, Timestamp = start.AddMinutes(index), Text = $"Run {index}", Trigger = "poll" },
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
            await timeline.EvaluateAsync("timeline => timeline.scrollTop = 0");
            var heldRow = rows.Filter(new() { HasText = "Question 70" });
            var offsetBefore = await ReadOffsetInTimelineAsync(heldRow);
            await page.GetByTestId("agent-timeline-show-earlier").ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(100, new() { Timeout = PageLoadTimeout });
            Assert.Equal(offsetBefore, await ReadOffsetInTimelineAsync(heldRow), 2.0);

            await timeline.EvaluateAsync("timeline => timeline.scrollTop = 0");
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

    [Fact]
    public async Task WhenAddingAndDeletingAMemoryEntry_ThenTheMemoryTableShowsEachChange()
    {
        // Arrange - the key is unique per run, so an entry left behind by an earlier run cannot satisfy the assertion.
        var key = "e2e/" + Guid.NewGuid().ToString("N");
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/{AgentPath}");
        await Assertions.Expect(page.GetByTestId("agent-chat-input")).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
        var agent = Assert.IsType<ClaudeCliAgent>(resolver.ResolveSubject("/" + AgentPath, PathStyle.Canonical));
        var table = page.GetByTestId("agent-memory");

        try
        {
            // Act & Assert
            await page.GetByRole(AriaRole.Tab, new() { Name = "Configuration" }).ClickAsync();
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
            var scriptPath = Path.Combine(scriptDirectory, "claude");
            await File.WriteAllTextAsync(scriptPath, createScript(scriptDirectory));
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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

        var storageName = AgentWatchPaths.GetStorageName(path);
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
