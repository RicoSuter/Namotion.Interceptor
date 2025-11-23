using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

public class OpcUaTestServer<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;
    private IInterceptorSubjectContext? _context;

    public TRoot? Root { get; private set; }

    public OpcUaTestServer(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot, 
        Action<IInterceptorSubjectContext, TRoot>? initializeDefaults = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddConsole();
        });

        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(builder.Services);

        Root = createRoot(_context);

        initializeDefaults?.Invoke(_context, Root);

        builder.Services.AddSingleton(Root);
        builder.Services.AddOpcUaServer<TRoot>("opc", rootName: "Root");

        _host = builder.Build();
        await _host.StartAsync();
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
                _output.WriteLine("Server host disposed");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error disposing server: {ex.Message}");
        }
    }
}