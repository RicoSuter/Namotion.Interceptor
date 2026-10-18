using System.Text.Json;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests;

public class SubjectMetadataExtensionsTests
{
    private static MetadataTestSubject CreateSubject()
        => new(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());

    [Fact]
    public void WhenSubjectHasAMetadataSubject_ThenItsSubjectMetadataIsRead()
    {
        // Arrange
        var subject = CreateSubject();
        var metadata = new SubjectMetadata { NavigationTitle = "Pump", Icon = "Water" };
        TestMetadataSubject.Attach(subject, metadata);

        // Act
        var read = subject.TryGetSubjectMetadata();

        // Assert
        Assert.Equal(metadata, read);
        Assert.IsType<TestMetadataSubject>(subject.TryGetMetadataSubject());
    }

    [Fact]
    public void WhenSubjectHasNoMetadataSubject_ThenItHasNoSubjectMetadata()
    {
        // Arrange
        var subject = CreateSubject();

        // Act
        var metadata = subject.TryGetSubjectMetadata();

        // Assert
        Assert.Null(metadata);
    }

    [Fact]
    public void WhenSubjectIsNotRegistered_ThenItHasNoMetadataSubject()
    {
        // Arrange
        var subject = new MetadataTestSubject();

        // Act
        var metadataSubject = subject.TryGetMetadataSubject();

        // Assert
        Assert.Null(metadataSubject);
    }
}

[InterceptorSubject]
public partial class MetadataTestSubject
{
    public partial string? Name { get; set; }
}

/// <summary>A metadata subject with fixed metadata, in the property where a storage puts its own.</summary>
public sealed class TestMetadataSubject(SubjectMetadata? metadata) : IMetadataSubject
{
    public SubjectMetadata? SubjectMetadata => metadata;

    public IInterceptorSubject GetOwner() => throw new NotSupportedException();

    public Task SetMetadataAsync(JsonElement values, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetOrderAsync(string[] names, CancellationToken cancellationToken) => Task.CompletedTask;

    public static void Attach(IInterceptorSubject subject, SubjectMetadata? metadata)
    {
        var metadataSubject = new TestMetadataSubject(metadata);
        subject.TryGetRegisteredSubject()!.AddProperty<IMetadataSubject>(IMetadataSubject.PropertyName, _ => metadataSubject);
    }
}
