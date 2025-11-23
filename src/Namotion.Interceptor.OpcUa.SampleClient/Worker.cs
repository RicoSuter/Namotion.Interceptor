using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.SamplesModel;

namespace Namotion.Interceptor.OpcUa.SampleClient;

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
            //_root.Number++;
            await Task.Delay(1000, stoppingToken);
        }
    }
}