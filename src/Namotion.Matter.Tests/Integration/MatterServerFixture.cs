using System.Diagnostics;
using System.Text;
using Namotion.Interceptor.Testing;
using Namotion.Matter.Server;

namespace Namotion.Matter.Tests.Integration;

[CollectionDefinition(Name)]
public sealed class MatterServerCollection : ICollectionFixture<MatterServerFixture>
{
    public const string Name = "Matter server";
}

/// <summary>Runs matterjs-server and a virtual light from the pinned Node packages for the whole collection.</summary>
public sealed class MatterServerFixture : IAsyncLifetime
{
    public const string LightManualCode = "34970112332";

    private readonly string _workingDirectory = Path.Combine(AppContext.BaseDirectory, "Integration", "node");
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"namotion-matter-{Guid.NewGuid():N}");
    private readonly StringBuilder _output = new();
    private Process? _server;
    private Process? _light;

    public Uri TcpUrl { get; private set; } = null!;

    public Uri UnixUrl { get; private set; } = null!;

    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDirectory);
        await InstallPackagesAsync();

        var port = LoopbackPorts.GetFreePort();
        // A Unix socket path is limited to about 100 bytes, so the socket stays at the top of the data directory.
        var socketPath = Path.Combine(_dataDirectory, "matter.sock");
        TcpUrl = new Uri($"ws://127.0.0.1:{port}/ws");
        UnixUrl = new Uri("unix://" + socketPath);

        // The bundled DCL seed trusts the virtual light's test PAA; --dcl-offline keeps commissioning from waiting
        // for a DCL refresh over the network.
        _server = Start(Path.Combine(_workingDirectory, "node_modules", ".bin", "matter-server"), $"--storage-path \"{Path.Combine(_dataDirectory, "server")}\" --port {port} " +
            $"--listen-address 127.0.0.1 --listen-address \"{socketPath}\" --enable-test-net-dcl --dcl-offline --disable-ota");
        _light = Start("node", $"virtual-light.mjs \"{Path.Combine(_dataDirectory, "light")}\" {LoopbackPorts.GetFreePort()}");

        await AsyncTestHelpers.WaitUntilAsync(() => Output.Contains("VIRTUAL-LIGHT-READY", StringComparison.Ordinal), () => Output);
        await AsyncTestHelpers.WaitUntilAsync(() =>
            Output.Contains($"Webserver listening on http://127.0.0.1:{port}", StringComparison.Ordinal) &&
            Output.Contains($"Webserver listening on unix socket {socketPath}", StringComparison.Ordinal), () => Output);

        // The server sends its information once its controller has started, which can take a while on the first start.
        await using var controller = await MatterServerController.ConnectAsync(TcpUrl, new MatterServerOptions { RequestTimeout = TestTimeouts.Default });
    }

    public Task DisposeAsync()
    {
        foreach (var process in new[] { _light, _server })
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            process?.Dispose();
        }

        Directory.Delete(_dataDirectory, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>Runs <c>npm ci</c> unless the installed packages already match the lock file.</summary>
    private async Task InstallPackagesAsync()
    {
        var lockFile = Path.Combine(_workingDirectory, "package-lock.json");
        var installedLockFile = Path.Combine(_workingDirectory, "node_modules", ".installed-package-lock.json");
        if (File.Exists(installedLockFile) && await File.ReadAllTextAsync(installedLockFile) == await File.ReadAllTextAsync(lockFile))
        {
            return;
        }

        // npm ci deletes node_modules first, so the marker only exists after a complete install.
        await RunToCompletionAsync("npm", "ci --no-audit --no-fund");
        File.Copy(lockFile, installedLockFile, overwrite: true);
    }

    private Process Start(string fileName, string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = _workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.OutputDataReceived += (_, line) => Append(line.Data);
        process.ErrorDataReceived += (_, line) => Append(line.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task RunToCompletionAsync(string fileName, string arguments)
    {
        using var process = Start(fileName, arguments);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{fileName} {arguments}' failed with exit code {process.ExitCode}:\n{Output}");
        }
    }

    private void Append(string? line)
    {
        if (line is not null)
        {
            lock (_output)
            {
                _output.AppendLine(line);
            }
        }
    }
}
