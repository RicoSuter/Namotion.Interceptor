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
        while (!stoppingToken.IsCancellationRequested)
        {
            //_root.Number++;
            await Task.Delay(1000, stoppingToken);
        }
    }
}