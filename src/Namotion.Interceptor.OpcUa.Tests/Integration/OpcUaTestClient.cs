using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

public class OpcUaTestClient<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private const string DefaultServerUrl = "opc.tcp://localhost:4840";

    private readonly ITestOutputHelper _output;
    private IHost? _host;

    public TRoot? Root { get; private set; }

    public OpcUaTestClient(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Func<TRoot, bool> isConnected,
        string serverUrl = DefaultServerUrl,
        bool enableLiveSync = false,
        bool enableRemoteNodeManagement = false,
        bool enablePeriodicResync = false)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddConsole();
        });

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(builder.Services);

        Root = createRoot(context);

        builder.Services.AddSingleton(Root);
        builder.Services.AddOpcUaSubjectClient<TRoot>(
            serverUrl, 
            "opc", 
            rootName: "Root",
            enableLiveSync: enableLiveSync,
            enableRemoteNodeManagement: enableRemoteNodeManagement,
            enablePeriodicResync: enablePeriodicResync);

        _host = builder.Build();
        await _host.StartAsync();

        // Wait for client to connect (15 seconds timeout for slower CI environments)
        for (var i = 0; i < 75; i++)
        {
            if (Root != null && isConnected(Root))
                return;

            await Task.Delay(200);
        }

        throw new XunitException("Could not sync with server.");
    }

    public async Task StopAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host != null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                    _host.Dispose();
                    _output.WriteLine("Client host disposed");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error disposing client: {ex.Message}");
        }
    }
}