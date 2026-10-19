using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests;

public class SubjectMetadataExtensionsTests
{
    [Fact]
    public void WhenSubjectIsNotRegistered_ThenItHasNoMetadata()
    {
        // Arrange
        var subject = new MetadataTestSubject();

        // Act
        var metadata = subject.GetMetadata();

        // Assert
        Assert.Null(metadata);
    }

    [Fact]
    public void WhenSubjectAndPropertyHaveMetadata_ThenTheirValuesAreRead()
    {
        // Arrange
        var subject = new MetadataTestSubject(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        TestMetadata.Set(subject, """{ "navTitle": "Pump" }""");
        var name = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MetadataTestSubject.Name))!;
        var values = TestMetadata.Parse("""{ "comment": "set by hand" }""");
        name.AddAttribute(KnownAttributes.Metadata, values.GetType(), _ => values, null);

        // Act
        var subjectMetadata = subject.GetMetadata();
        var propertyMetadata = name.GetMetadata();

        // Assert
        Assert.Equal("Pump", subjectMetadata.GetString(MetadataKeys.NavigationTitle));
        Assert.Equal("set by hand", propertyMetadata.GetString("comment"));
    }

    [Fact]
    public void WhenPropertiesAreChecked_ThenOnlyTheMetadataOfTheSubjectAndOfAPropertyIsMetadata()
    {
        // Arrange
        var subject = new MetadataTestSubject(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        TestMetadata.Set(subject, """{ "navTitle": "Pump" }""");
        var registered = subject.TryGetRegisteredSubject()!;
        var name = registered.TryGetProperty(nameof(MetadataTestSubject.Name))!;
        var attribute = name.AddAttribute(KnownAttributes.Metadata, typeof(object), _ => null, null);

        // Act & Assert
        Assert.True(registered.TryGetProperty(KnownAttributes.Metadata)!.IsMetadata());
        Assert.True(attribute.IsMetadata());
        Assert.False(name.IsMetadata());
    }
}

[InterceptorSubject]
public partial class MetadataTestSubject
{
    public partial string? Name { get; set; }
}
