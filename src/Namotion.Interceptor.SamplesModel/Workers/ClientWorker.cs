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
        // Each batch of 400 persons is updated in parallel

        var batchCount = 50;
        var batchInterval = TimeSpan.FromMilliseconds(1000 / batchCount); // 20ms

        using var timer = new PeriodicTimer(batchInterval);
        var batchIndex = 0;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var persons = _root.Persons; // Read each tick (populated by Welcome message)
            if (persons.Length == 0)
                continue;

            var actualPersonsPerBatch = persons.Length / batchCount;
            var startIndex = batchIndex * actualPersonsPerBatch;
            var endIndex = (batchIndex == batchCount - 1)
                ? persons.Length  // Last batch gets any remainder
                : startIndex + actualPersonsPerBatch;

            Parallel.For(startIndex, endIndex, i =>
            {
                persons[i].LastName = Stopwatch.GetTimestamp().ToString();
            });

            batchIndex = (batchIndex + 1) % batchCount;
        }
    }
}
