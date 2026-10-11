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

    /// <summary>
    /// Points the test agent at a script that replays <paramref name="output"/>, sends a chat message from its
    /// page and runs <paramref name="assert"/> on the page, then restores the agent and deletes its data files.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private async Task RunChatAsync(string output, Func<IPage, Task> assert)
    {
        var scriptDirectory = Directory.CreateTempSubdirectory("e2e-fake-claude").FullName;
        try
        {
            var scriptPath = Path.Combine(scriptDirectory, "claude");
            await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\ncat > /dev/null\ncat <<'EOF'\n" + output.Replace("\r\n", "\n") + "\nEOF\n");
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

                await assert(page);
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
