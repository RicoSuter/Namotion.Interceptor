using System.Runtime.Versioning;
using HomeBlaze.Abstractions;
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
        // streaming more, so the test controls when content grows. Deleting the directory ends the waits.
        static string Delta(string text) =>
            $$$"""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{text}}}"}},"session_id":"e2e-session"}""";

        var firstLines = string.Concat(Enumerable.Range(1, 40).Select(index => $"line {index}\\n\\n"));
        var moreLines = string.Concat(Enumerable.Range(1, 20).Select(index => $"more {index}\\n\\n"));
        string CreateScript(string directory) =>
            "#!/bin/sh\ncat > /dev/null\n" +
            "cat <<'EOF'\n" +
            """{"type":"system","subtype":"init","session_id":"e2e-session","tools":[],"mcp_servers":[]}""" + "\n" +
            Delta(firstLines) + "\nEOF\n" +
            $"while [ -d '{directory}' ] && [ ! -f '{directory}/more' ]; do sleep 0.05; done\n" +
            "cat <<'EOF'\n" + Delta(moreLines) + "\nEOF\n" +
            $"while [ -d '{directory}' ] && [ ! -f '{directory}/finish' ]; do sleep 0.05; done\n" +
            "cat <<'EOF'\n" +
            """{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"e2e-session","usage":{"input_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}""" +
            "\nEOF\n";

        // Act & Assert
        await RunChatAsync(CreateScript, async (page, directory) =>
        {
            var streaming = page.GetByTestId("agent-streaming");
            var timeline = await page.GetByTestId("agent-timeline").ElementHandleAsync();

            // At the bottom, the streamed lines keep the timeline scrolled to the newest one.
            await Assertions.Expect(streaming).ToContainTextAsync("line 40", new() { Timeout = ReplyTimeout });
            await page.WaitForFunctionAsync(
                "timeline => timeline.scrollHeight > timeline.clientHeight + 200 && timeline.scrollHeight - timeline.scrollTop - timeline.clientHeight <= 32",
                timeline, new() { Timeout = ReplyTimeout });

            // Scrolled up, further lines leave the view where the reader put it.
            await page.GetByTestId("agent-timeline").HoverAsync();
            await page.Mouse.WheelAsync(0, -300);
            await page.WaitForFunctionAsync(
                "timeline => timeline.scrollHeight - timeline.scrollTop - timeline.clientHeight > 200", timeline, new() { Timeout = ReplyTimeout });
            var contentOffset = await ReadContentOffsetAfterLayoutAsync(page, timeline);

            await File.WriteAllTextAsync(Path.Combine(directory, "more"), string.Empty);
            await Assertions.Expect(streaming).ToContainTextAsync("more 20", new() { Timeout = ReplyTimeout });
            Assert.Equal(contentOffset, await ReadContentOffsetAfterLayoutAsync(page, timeline), 1.0);

            await File.WriteAllTextAsync(Path.Combine(directory, "finish"), string.Empty);
            await Assertions.Expect(page.GetByTestId("agent-reply").Last).ToContainTextAsync("done", new() { Timeout = ReplyTimeout });
        });
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
            var scriptPath = Path.Combine(scriptDirectory, "claude");
            await File.WriteAllTextAsync(scriptPath, createScript(scriptDirectory));
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // The page shows the chat only once the storage has loaded the agent, which can still be under way
            // right after the host started, so the agent is resolved after that.
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{AgentPath}");
            var input = page.GetByTestId("agent-chat-input").Locator("textarea");
            await Assertions.Expect(input).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });

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
