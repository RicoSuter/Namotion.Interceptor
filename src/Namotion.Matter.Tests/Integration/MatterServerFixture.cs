using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Namotion.Interceptor.Testing;
using Namotion.Matter.Server;

namespace Namotion.Matter.Tests.Integration;

[CollectionDefinition(Name)]
public sealed class MatterServerCollection : ICollectionFixture<MatterServerFixture>
{
    public const string Name = "Matter server";
}

/// <summary>
/// Runs matterjs-server and a virtual light from the pinned Node packages for the whole collection. The light gets a
/// random passcode and discriminator, so it does not collide with another commissionable test device on the network.
/// </summary>
public sealed partial class MatterServerFixture : IAsyncLifetime
{
    // The Matter specification forbids these passcodes because they are trivial to guess.
    private static readonly HashSet<uint> InvalidPasscodes =
        [0, 11111111, 22222222, 33333333, 44444444, 55555555, 66666666, 77777777, 88888888, 99999999, 12345678, 87654321];

    private readonly string _workingDirectory = Path.Combine(AppContext.BaseDirectory, "Integration", "node");
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"namotion-matter-{Guid.NewGuid():N}");
    private readonly StringBuilder _output = new();
    private Process? _server;
    private Process? _light;

    public Uri TcpUrl { get; private set; } = null!;

    public Uri UnixUrl { get; private set; } = null!;

    /// <summary>Gets the light's setup passcode.</summary>
    public uint LightPasscode { get; } = CreatePasscode();

    /// <summary>Gets the light's 12-bit discriminator; 3840, which examples and test devices use, is avoided.</summary>
    public ushort LightDiscriminator { get; } = CreateDiscriminator();

    /// <summary>Gets the light's manual pairing code, which the light reports once it is commissionable.</summary>
    public string LightManualCode { get; private set; } = null!;

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
        // xUnit does not reliably dispose a fixture whose initialization failed, so the processes are stopped here.
        try
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
            _light = Start("node", string.Create(CultureInfo.InvariantCulture,
                $"virtual-light.mjs \"{Path.Combine(_dataDirectory, "light")}\" {LoopbackPorts.GetFreePort()} {LightPasscode} {LightDiscriminator}"));

            await AsyncTestHelpers.WaitUntilAsync(() => LightReadyPattern().IsMatch(Output), () => Output);
            LightManualCode = LightReadyPattern().Match(Output).Groups[1].Value;
            await AsyncTestHelpers.WaitUntilAsync(() =>
                Output.Contains($"Webserver listening on http://127.0.0.1:{port}", StringComparison.Ordinal) &&
                Output.Contains($"Webserver listening on unix socket {socketPath}", StringComparison.Ordinal), () => Output);

            // The server sends its information once its controller has started, which can take a while on the first start.
            await using var controller = await MatterServerController.ConnectAsync(TcpUrl, new MatterServerOptions { ConnectTimeout = TestTimeouts.Default });
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public Task DisposeAsync()
    {
        Stop();
        return Task.CompletedTask;
    }

    /// <summary>Kills the processes and deletes the data directory; safe to call more than once.</summary>
    private void Stop()
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

        _light = null;
        _server = null;
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private static uint CreatePasscode()
    {
        uint passcode;
        do
        {
            passcode = (uint)RandomNumberGenerator.GetInt32(1, 99999999);
        }
        while (InvalidPasscodes.Contains(passcode));

        return passcode;
    }

    private static ushort CreateDiscriminator()
    {
        ushort discriminator;
        do
        {
            discriminator = (ushort)RandomNumberGenerator.GetInt32(0, 4096);
        }
        while (discriminator == 3840);

        return discriminator;
    }

    [GeneratedRegex(@"VIRTUAL-LIGHT-READY (\d{11})")]
    private static partial Regex LightReadyPattern();

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
