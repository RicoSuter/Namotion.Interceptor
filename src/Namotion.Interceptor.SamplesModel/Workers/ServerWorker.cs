using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.SamplesModel.Workers;

public class ServerWorker : BackgroundService
{
    private readonly Root _root;

    public ServerWorker(Root root)
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