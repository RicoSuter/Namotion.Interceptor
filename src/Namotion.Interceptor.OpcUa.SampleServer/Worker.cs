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
            if (_root.Person != null)
            {
                _root.Person.FirstName = Stopwatch.GetTimestamp().ToString();
            }
            
            await Task.Delay(1000);
        }
    }
}