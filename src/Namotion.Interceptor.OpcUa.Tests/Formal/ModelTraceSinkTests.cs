using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Namotion.Interceptor.Diagnostics;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Formal;

public class ModelTraceSinkTests
{
    [Fact]
    public void WhenTransitionsEmitted_ThenSnapshotsAreFoldedAndCoverNormalized()
    {
        // Arrange
        var file = Path.GetTempFileName();

        // Act
        using (new ModelTraceSink(file))
        {
            ModelTrace.Set("state", "Connecting"); ModelTrace.Commit();
            ModelTrace.Set("state", "SessionActive"); ModelTrace.SetItem("cover", "n1", "Subscribed"); ModelTrace.Commit();
        }

        // Assert
        var line = File.ReadAllText(file).TrimEnd('\n');
        using var doc = JsonDocument.Parse(line);
        var states = doc.RootElement;
        Assert.Equal(3, states.GetArrayLength()); // Init + Connect + Activate
        Assert.Equal("Disconnected", states[0].GetProperty("state").GetString());
        Assert.Equal("Retrying", states[0].GetProperty("cover").GetProperty("n1").GetString());
        Assert.Equal("SessionActive", states[2].GetProperty("state").GetString());
        Assert.Equal("Subscribed", states[2].GetProperty("cover").GetProperty("n1").GetString());
    }

    [Fact]
    [Trait("Category", "Formal")]
    public void WhenConnectActivateEmitted_ThenSinkOutputConformsToModel()
    {
        // Arrange
        var repoRoot = FindRepoRoot();
        var file = Path.GetTempFileName();

        // Act
        using (new ModelTraceSink(file))
        {
            ModelTrace.Set("state", "Connecting"); ModelTrace.Commit();
            ModelTrace.Set("state", "SessionActive"); ModelTrace.SetItem("cover", "n1", "Subscribed"); ModelTrace.Commit();
        }

        // Assert: the model checker accepts the sink's own output
        var psi = new ProcessStartInfo("bash", $"tools/tla/check-traces.sh \"{file}\" docs/formal/opcua-client/OpcUaClient.tla")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"check-traces rejected the sink output:\n{output}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "tla", "check-traces.sh")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
