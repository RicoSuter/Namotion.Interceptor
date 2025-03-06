using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceHandlerTests
{
    [Fact]
    public async Task WhenSubjectImplementsIHostedService_ThenItIsStartedAndStopped()
    {
        // Arrange
        PersonWithBackgroundService person = null!;

        // Act
        await RunAsync(async context =>
        {
            person = new PersonWithBackgroundService(context);
            await Task.Delay(100);
            
            // Assert
            Assert.Equal("John", person.FirstName);
            Assert.Equal("Doe", person.LastName);
        });
        
        await Task.Delay(100);
        Assert.Equal("Disposed", person!.FirstName);
    }
    
    [Fact]
    public async Task WhenHostedServiceIsAttachedToSubject_ThenHostedServiceIsStarted()
    {
        // Arrange
        Person person = null!;
        await RunAsync(async context =>
        {
            person = new Person(context);
            
            // Act
            var hostedService = new PersonBackgroundService(person);
            person.AttachHostedService(hostedService);

            await Task.Delay(100);

            // Assert
            Assert.Equal("John", person.FirstName);
            Assert.Equal("Doe", person.LastName);
        });
        
        await Task.Delay(100);
        Assert.Equal("Disposed", person!.FirstName);
    }
    
    [Fact]
    public async Task WhenHostedServiceIsDetachedFromSubject_ThenHostedServiceIsStopped()
    {
        // Arrange
        Person person = null!;
        await RunAsync(async context =>
        {
            person = new Person(context);
         
            var hostedService = new PersonBackgroundService(person);
            person.AttachHostedService(hostedService);
            var attachedHostedServices = person.TryGetAttachedHostedServices();
            
            await Task.Delay(100);
            
            Assert.Equal("John", person.FirstName);
            Assert.Equal("Doe", person.LastName);
            Assert.Single(attachedHostedServices!);

            // Act
            person.DetachHostedService(hostedService);
            attachedHostedServices = person.TryGetAttachedHostedServices();

            // Assert
            await Task.Delay(100);
            Assert.Equal("Disposed", person!.FirstName);
            Assert.Empty(attachedHostedServices!);
        });
    }

    private static async Task RunAsync(Func<IInterceptorSubjectContext, Task> action)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        try
        {
            var builder = Host.CreateApplicationBuilder();
        
            var context = InterceptorSubjectContext
                .Create()
                .WithLifecycle()
                .WithHostedServices(builder.Services);

            var host = builder.Build();
            _ = host.RunAsync(cancellationTokenSource.Token);

            await action(context);
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
        }
    }
}