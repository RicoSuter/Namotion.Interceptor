using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.OpcUa.SampleModel;

namespace Namotion.Interceptor.OpcUa.SampleServer;

public class Worker : BackgroundService
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
            foreach (var person in _root.Persons)
            {
                person.FirstName = Stopwatch.GetTimestamp().ToString();
            }

            await Task.Delay(1000, stoppingToken);
        }
    }
}