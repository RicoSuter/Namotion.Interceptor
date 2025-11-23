using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Mqtt;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.SamplesModel;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking;

const int PersonCount = 10_000;
const string BrokerHost = "localhost";
const int BrokerPort = 1883;

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry();

var root = Root.CreateWithPersons(context, PersonCount);

builder.Services.AddSingleton(root);

builder.Services.AddMqttSubjectClient(
    _ => root,
    _ => new MqttClientConfiguration
    {
        BrokerHost = BrokerHost,
        BrokerPort = BrokerPort,
        PathProvider = new AttributeBasedSourcePathProvider("mqtt", "/", null)
    });

builder.Services.AddHostedService<Worker>();

using var performanceProfiler = new PerformanceProfiler(context, "Client");
var host = builder.Build();
host.Run();

internal class Worker : BackgroundService
{
    private readonly Root _root;

    public Worker(Root root)
    {
        _root = root;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }
}
