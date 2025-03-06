using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting.Tests.Models;

public class PersonBackgroundService : BackgroundService
{
    private readonly Person _person;

    public PersonBackgroundService(Person person)
    {
        _person = person;
    }
        
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _person.FirstName = "John";
            _person.LastName = "Doe";
                
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            _person.FirstName = "Disposed";
        }
    }
}