using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests;

public class HostedSubjectConfigurationTests
{
    [Fact]
    public async Task WhenHostedSubjectHasAConfigurationCallback_ThenItsServiceStartsAfterConfiguration()
    {
        // Arrange
        var services = new ServiceCollection().AddLogging();
        var context = InterceptorSubjectContext.Create().WithHostedServices(services);
        var handler = Assert.Single(context.GetServices<IHostedService>());
        services.AddSingleton(context);
        services.AddSubject<ConfiguredHostedSubject>(subject =>
        {
            handler.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            subject.Configuration = "configured";
        }, contextResolver: serviceProvider => serviceProvider.GetRequiredService<IInterceptorSubjectContext>());
        await using var provider = services.BuildServiceProvider();

        try
        {
            // Act
            var subject = provider.GetRequiredService<ConfiguredHostedSubject>();

            // Assert
            Assert.Equal("configured", await subject.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await handler.StopAsync(CancellationToken.None);
        }
    }
}

[InterceptorSubject]
public partial class ConfiguredHostedSubject : IHostedService
{
    public string Configuration { get; set; } = string.Empty;
    public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Started.TrySetResult(Configuration);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
