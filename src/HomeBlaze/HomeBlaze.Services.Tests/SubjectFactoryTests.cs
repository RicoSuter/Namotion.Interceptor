using HomeBlaze.Services.Tests.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Hosting;

namespace HomeBlaze.Services.Tests;

public class SubjectFactoryTests
{
    [Fact]
    public void WhenCreatedSubjectHasAHostedService_ThenItIsNotActivated()
    {
        // Arrange
        var factory = new SubjectFactory(new ServiceCollection().BuildServiceProvider());

        // Act
        var subject = factory.CreateSubject<ActivatableTestSubject>();

        // Assert
        Assert.Empty(subject.GetHostedServiceAttachments());
    }

    [Fact]
    public void WhenCreatedSubjectIsActivated_ThenItsHostedServiceIsAttached()
    {
        // Arrange
        var factory = new SubjectFactory(new ServiceCollection().BuildServiceProvider());
        var subject = factory.CreateSubject<ActivatableTestSubject>();

        // Act
        var attachment = factory.ActivateHostedService(subject);

        // Assert
        Assert.NotNull(attachment);
        Assert.Single(subject.GetHostedServiceAttachments());
    }

    [Fact]
    public void WhenSubjectWithoutHostedServiceIsActivated_ThenNothingIsAttached()
    {
        // Arrange
        var factory = new SubjectFactory(new ServiceCollection().BuildServiceProvider());
        var subject = factory.CreateSubject<TestSubject>();

        // Act
        var attachment = factory.ActivateHostedService(subject);

        // Assert
        Assert.Null(attachment);
        Assert.Empty(subject.GetHostedServiceAttachments());
    }
}
