using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.SamplesModel;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking;

const int PersonCount = 10_000;
const int MqttPort = 1883;

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry();

var root = Root.CreateWithPersons(context, PersonCount);

builder.Services.AddSingleton(root);

builder.Services.AddMqttSubjectServer(
    _ => root,
    _ => new MqttServerConfiguration
    {
        BrokerHost = "localhost",
        BrokerPort = MqttPort,
        PathProvider = new AttributeBasedSourcePathProvider("mqtt", "/", null)
    });

builder.Services.AddHostedService<Worker>();

using var performanceProfiler = new PerformanceProfiler(context, "Server");
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
        // Expected updates/second = number of persons * 2 / delay

        var delay = TimeSpan.FromSeconds(1);
        var lastChange = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            var mod = _root.Persons.Length / 50;
            var now = DateTimeOffset.UtcNow;
            if (now - lastChange > delay)
            {
                lastChange = lastChange.AddSeconds(1);

                for (var index = 0; index < _root.Persons.Length; index++)
                {
                    var person = _root.Persons[index];

                    // Triggers 2 changes: FirstName and FullName
                    person.FirstName = Stopwatch.GetTimestamp().ToString();

                    if (index % mod == 0) // distribute updates over approx. 0.5s
                    {
                        await Task.Delay(10, stoppingToken);
                    }
                }
            }

            await Task.Delay(10, stoppingToken);
        }
    }
}
