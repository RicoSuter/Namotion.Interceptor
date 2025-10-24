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
        // expected updates/second = number of persons * 2 / delay
        
        var delay = TimeSpan.FromSeconds(1);
        var lastChange = DateTimeOffset.Now;
        while (!stoppingToken.IsCancellationRequested)
        {
            var mod = _root.Persons.Length / 50;
            var now = DateTimeOffset.Now;
            if (now - lastChange > delay)
            {
                lastChange = lastChange.AddSeconds(1);

                for (var index = 0; index < _root.Persons.Length; index++)
                {
                    var person = _root.Persons[index];
                    // this triggers 2 changes
                    person.FirstName = Stopwatch.GetTimestamp().ToString();

                    if (index % mod == 0) // distribute updates over approx. 0.5s
                        await Task.Delay(10, stoppingToken);
                }
            }

            await Task.Delay(10, stoppingToken);
        }
    }
}