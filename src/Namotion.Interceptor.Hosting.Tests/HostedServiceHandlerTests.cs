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
        await RunWithAppLifecycleAsync(async context =>
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
        await RunWithAppLifecycleAsync(async context =>
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
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new Person(context);
         
            var hostedService = new PersonBackgroundService(person);
            person.AttachHostedService(hostedService);
            var attachedHostedServices = person.GetAttachedHostedServices();
            
            await Task.Delay(100);
            
            Assert.Equal("John", person.FirstName);
            Assert.Equal("Doe", person.LastName);
            Assert.Single(attachedHostedServices!);

            // Act
            person.DetachHostedService(hostedService);
            attachedHostedServices = person.GetAttachedHostedServices();

            // Assert
            await Task.Delay(100);
            Assert.Equal("Disposed", person!.FirstName);
            Assert.Empty(attachedHostedServices!);
        });
    }

    [Fact]
    public async Task WhenSubjectServiceIsDetached_ThenHostedServiceIsStopped()
    {
        // Arrange
        Person person = null!;
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new Person(context);
         
            var hostedService = new PersonBackgroundService(person);
            person.AttachHostedService(hostedService);
            var attachedHostedServices = person.GetAttachedHostedServices();
            
            await Task.Delay(100);
            Assert.Single(attachedHostedServices!);

            // Act
            ((IInterceptorSubject)person).Context.RemoveFallbackContext(context);
            attachedHostedServices = person.GetAttachedHostedServices();

            // Assert
            await Task.Delay(100);
            Assert.Equal("Disposed", person!.FirstName);
            Assert.Empty(attachedHostedServices!); // the service has been stopped and
                                                   // removed from list (not allowed to restart again anyway)
        });
    }

    private static async Task RunWithAppLifecycleAsync(Func<IInterceptorSubjectContext, Task> action)
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