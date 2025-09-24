using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Namotion.Interceptor.OpcUa.Tests;

[Collection("OPC UA Integration")]
public class OpcUaServerClientIntegrationTests : IAsyncDisposable
{
    private const string ServerUrl = "opc.tcp://localhost:4840";

    private readonly ITestOutputHelper _output;
    private IHost? _serverHost;
    private IHost? _clientHost;
    private TestRoot? _serverRoot;
    private TestRoot? _clientRoot;
    private IInterceptorSubjectContext? _serverContext;

    public OpcUaServerClientIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ServerClient_ReadWriteOperations_ShouldWork()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            // Act & Assert
            Assert.NotNull(_serverRoot);
            Assert.NotNull(_clientRoot);

            // Test string property
            _serverRoot.Name = "Updated Server Name";
            await Task.Delay(1000);
            Assert.Equal("Updated Server Name", _clientRoot.Name);
            
            // Test numeric property
            _serverRoot.Number = 123.45m;
            await Task.Delay(1000);
            Assert.Equal(123.45m, _clientRoot.Number);
        }
        finally
        {
            await (_serverHost?.StopAsync() ?? Task.CompletedTask);
            await (_clientHost?.StopAsync() ?? Task.CompletedTask);
        }
    }

    [Fact]
    public async Task ServerClient_ArrayValueRank_ShouldBeCorrect()
    {
        try
        {
            // Arrange - Start Server and Client
            await StartServerAsync();
            await StartClientAsync();

            // Act & Assert - Test basic array synchronization to validate valueRank
            Assert.NotNull(_serverRoot);
            Assert.NotNull(_clientRoot);
    
            // Test just one simple integer array
            _output.WriteLine($"Server initial ScalarNumbers: [{string.Join(", ", _serverRoot.ScalarNumbers)}]");
            _output.WriteLine($"Client initial ScalarNumbers: [{string.Join(", ", _clientRoot.ScalarNumbers)}]");
    
            var newNumbers = new[] { 100, 200, 300 };
            _serverRoot.ScalarNumbers = newNumbers;
            _output.WriteLine($"Server updated ScalarNumbers: [{string.Join(", ", _serverRoot.ScalarNumbers)}]");
    
            // Wait longer for synchronization
            await Task.Delay(8000);
    
            _output.WriteLine($"Client ScalarNumbers after update: [{string.Join(", ", _clientRoot.ScalarNumbers)}]");
    
            // If this fails, it indicates that either:
            // 1. Server-client sync is not working, OR
            // 2. The valueRank is incorrect and arrays can't sync properly
            Assert.Equal(newNumbers, _clientRoot.ScalarNumbers);
            _output.WriteLine($"✓ Basic array sync: [{string.Join(", ", _clientRoot.ScalarNumbers)}]");
        }
        finally
        {
            await (_serverHost?.StopAsync() ?? Task.CompletedTask);
            await (_clientHost?.StopAsync() ?? Task.CompletedTask);
        }
    }

    private async Task StartServerAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddConsole();
        });

        _serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(builder.Services);

        _serverRoot = new TestRoot(_serverContext)
        {
            Connected = true,
            Name = "Foo bar",
            ScalarNumbers = [10, 20, 30, 40, 50],
            ScalarStrings = ["Server", "Test", "Array"],
            NestedNumbers = [[100, 200], [300, 400]],
            People =
            [
                new TestPerson(_serverContext) { FirstName = "John", LastName = "Server", Scores = [85.5, 92.3] },
                new TestPerson(_serverContext) { FirstName = "Jane", LastName = "Test", Scores = [88.1, 95.7] }
            ]
        };

        builder.Services.AddSingleton(_serverRoot);
        builder.Services.AddOpcUaSubjectServer<TestRoot>("opc", rootName: "Root");

        _serverHost = builder.Build();
        await _serverHost.StartAsync();
    }

    private async Task StartClientAsync()
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

        _clientRoot = new TestRoot(context);

        builder.Services.AddSingleton(_clientRoot);
        builder.Services.AddOpcUaSubjectClient<TestRoot>(ServerUrl, "opc", rootName: "Root");

        _clientHost = builder.Build();
        await _clientHost.StartAsync();
        
        // Wait for client to connect
        for (var i = 0; i < 30; i++)
        {
            if (_clientRoot?.Connected == true)
                return;
            
            await Task.Delay(1000);
        }

        throw new XunitException("Could not sync with server.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_clientHost != null)
            {
                await _clientHost.StopAsync(TimeSpan.FromSeconds(5));
                _clientHost.Dispose();
                _output.WriteLine("Client host disposed");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error disposing client: {ex.Message}");
        }

        try
        {
            if (_serverHost != null)
            {
                await _serverHost.StopAsync(TimeSpan.FromSeconds(5));
                _serverHost.Dispose();
                _output.WriteLine("Server host disposed");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error disposing server: {ex.Message}");
        }

        // Give time for resources to be released
        await Task.Delay(1000);
    }
}