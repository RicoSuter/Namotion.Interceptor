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
        // Expected updates/second = number of persons (20k) = 1.2M per minute
        // Updates are distributed across 50 batches per second (every 20ms)
        // Each batch of 400 persons is updated in parallel

        var batchCount = 50;
        var personsPerBatch = _root.Persons.Length / batchCount;
        var batchInterval = TimeSpan.FromMilliseconds(1000 / batchCount); // 20ms

        using var timer = new PeriodicTimer(batchInterval);
        var batchIndex = 0;
        var persons = _root.Persons;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var startIndex = batchIndex * personsPerBatch;
            var endIndex = (batchIndex == batchCount - 1)
                ? persons.Length  // Last batch gets any remainder
                : startIndex + personsPerBatch;

            Parallel.For(startIndex, endIndex, i =>
            {
                persons[i].FirstName = Stopwatch.GetTimestamp().ToString();
            });

            batchIndex = (batchIndex + 1) % batchCount;
        }
    }
}
