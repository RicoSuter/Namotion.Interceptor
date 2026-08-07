using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

public class WithHostedServicesTests
{
    [Fact]
    public async Task WhenTwoContextsShareOneServiceCollection_ThenBothHandlersRun()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();

        var firstContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var secondContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Act
            var firstPerson = new Person(firstContext);
            var secondPerson = new Person(secondContext);
            firstPerson.AttachHostedService(() => new PersonBackgroundService(firstPerson));
            secondPerson.AttachHostedService(() => new PersonBackgroundService(secondPerson));

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => firstPerson.FirstName == "John");
            await AsyncTestHelpers.WaitUntilAsync(() => secondPerson.FirstName == "John",
                message: "The second context's handler was dropped by TryAddEnumerable dedupe.");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
