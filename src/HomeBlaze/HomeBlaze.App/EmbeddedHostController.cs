using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.App;

/// <summary>Runs HomeBlaze inside this process and reports where it listens.</summary>
public sealed class EmbeddedHostController : IAsyncDisposable
{
    /// <summary>
    /// Time allowed for a graceful stop. The OPC UA server needs most of it to shut down cleanly, which is
    /// the same reason the end-to-end harness waits this long.
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Application name the host runs under. MapStaticAssets resolves its manifest by
    /// IHostEnvironment.ApplicationName, which otherwise defaults to this process's entry assembly, so it
    /// would look for a HomeBlaze.App manifest (or a testhost one) that no project ever generates. Taken
    /// from the assembly that does generate the manifest, so an assembly rename cannot silently break it.
    /// </summary>
    private static readonly string HostingApplicationName =
        typeof(HomeBlazeWebApplication).Assembly.GetName().Name!;

    private WebApplication? _application;

    /// <summary>Services of the running host, or null before a successful start.</summary>
    public IServiceProvider? Services => _application?.Services;

    /// <summary>Starts the host and returns the address it bound.</summary>
    /// <exception cref="InvalidOperationException">A host is already running, or no address was bound.</exception>
    public async Task<string> StartAsync(ResolvedWorkspace workspace, CancellationToken cancellationToken)
    {
        if (_application is not null)
        {
            throw new InvalidOperationException("The embedded host is already running.");
        }

        var options = new HomeBlazeWebApplicationOptions
        {
            RootConfigurationFile = workspace.RootConfigurationFile,
            SeedDirectory = workspace.SeedDirectory.Length == 0 ? null : workspace.SeedDirectory,
            SuppressSeeding = workspace.SeedDirectory.Length == 0,

            // Port zero: the operating system picks a free port, so two instances on different folders never
            // collide and nothing has to be configured.
            Urls = ["http://127.0.0.1:0"],

            // MapStaticAssets() locates its manifest file by IHostEnvironment.ApplicationName, which otherwise
            // defaults to this process's own entry assembly (HomeBlaze.App, or the test host). Pinning it to
            // HomeBlaze finds the manifest HomeBlaze's own build produced, regardless of which process hosts it.
            CommandLineArguments = [$"--applicationName={HostingApplicationName}"]
        };

        var application = await HomeBlazeWebApplication.CreateAsync(options, cancellationToken);

        try
        {
            await application.StartAsync(cancellationToken);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }

        _application = application;

        var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return addresses?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The embedded host bound no address.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is null)
        {
            return;
        }

        var application = _application;
        _application = null;

        try
        {
            // WebApplication declares its own StopAsync(CancellationToken) overload, which hides the
            // IHost.StopAsync(TimeSpan) extension overload at the call site, so the timeout is applied
            // through a token instead.
            using var timeoutSource = new CancellationTokenSource(ShutdownTimeout);
            await application.StopAsync(timeoutSource.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            // A host that will not stop in time must not keep the window open.
        }

        await application.DisposeAsync();
    }
}
