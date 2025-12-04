using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.SamplesModel.Workers;

public class ClientWorker : BackgroundService
{
    private readonly Root _root;

    public ClientWorker(Root root)
    {
        _root = root;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Expected updates/second = number of persons (20k) = 1.2M per minute
        // Updates are distributed across 50 batches per second (every 20ms)

        var batchCount = 50;
        var personsPerBatch = _root.Persons.Length / batchCount;
        var batchInterval = TimeSpan.FromMilliseconds(1000 / batchCount); // 20ms

        using var timer = new PeriodicTimer(batchInterval);
        var batchIndex = 0;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var startIndex = batchIndex * personsPerBatch;
            var endIndex = (batchIndex == batchCount - 1)
                ? _root.Persons.Length  // Last batch gets any remainder
                : startIndex + personsPerBatch;

            for (var i = startIndex; i < endIndex; i++)
            {
                _root.Persons[i].LastName = Stopwatch.GetTimestamp().ToString();
            }

            batchIndex = (batchIndex + 1) % batchCount;
        }
    }
}