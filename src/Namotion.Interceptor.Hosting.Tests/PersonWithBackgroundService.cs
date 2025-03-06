using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests
{
    [InterceptorSubject]
    public partial class PersonWithBackgroundService : BackgroundService
    {
        public partial string? FirstName { get; set; }

        public partial string? LastName { get; set; }
        
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                FirstName = "John";
                LastName = "Doe";
                
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                FirstName = "Disposed";
            }
        }
    }
}