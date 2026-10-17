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
    public void WhenMetadataIsSet_ThenItIsRead()
    {
        // Arrange
        var subject = CreateSubject();
        var metadata = new SubjectMetadata("Pump", "Water", NavigationLocation.AppBar, AppBarAlignment.Right);

        // Act
        subject.SetSubjectMetadata(metadata);

        // Assert
        Assert.Equal(metadata, subject.TryGetSubjectMetadata());
    }

    [Fact]
    public void WhenMetadataIsSetAgain_ThenTheNewValueIsRead()
    {
        // Arrange
        var subject = CreateSubject();
        subject.SetSubjectMetadata(new SubjectMetadata("Pump", null, null, null));

        // Act
        subject.SetSubjectMetadata(new SubjectMetadata("Main pump", null, null, null));

        // Assert
        Assert.Equal("Main pump", subject.TryGetSubjectMetadata()?.NavigationTitle);
    }

    [Fact]
    public void WhenMetadataIsSetToNull_ThenNoneIsRead()
    {
        // Arrange
        var subject = CreateSubject();
        subject.SetSubjectMetadata(new SubjectMetadata("Pump", null, null, null));

        // Act
        subject.SetSubjectMetadata(null);

        // Assert
        Assert.Null(subject.TryGetSubjectMetadata());
    }

    [Fact]
    public void WhenNullIsSetOnSubjectWithoutMetadata_ThenNoPropertyIsAdded()
    {
        // Arrange
        var subject = CreateSubject();

        // Act
        subject.SetSubjectMetadata(null);

        // Assert
        Assert.Null(subject.TryGetRegisteredSubject()!.TryGetProperty(SubjectMetadata.PropertyName));
    }

    [Fact]
    public void WhenSubjectIsNotRegistered_ThenSettingMetadataDoesNothing()
    {
        // Arrange
        var subject = new MetadataTestSubject();

        // Act
        subject.SetSubjectMetadata(new SubjectMetadata("Pump", null, null, null));

        // Assert
        Assert.Null(subject.TryGetSubjectMetadata());
    }

    [Fact]
    public void WhenPropertyIsChecked_ThenOnlyTheMetadataPropertyIsSubjectMetadata()
    {
        // Arrange
        var subject = CreateSubject();
        subject.SetSubjectMetadata(new SubjectMetadata("Pump", null, null, null));
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var isMetadata = registered.TryGetProperty(SubjectMetadata.PropertyName)!.IsSubjectMetadata();
        var isNameMetadata = registered.TryGetProperty(nameof(MetadataTestSubject.Name))!.IsSubjectMetadata();

        // Assert
        Assert.True(isMetadata);
        Assert.False(isNameMetadata);
    }
}

[InterceptorSubject]
public partial class MetadataTestSubject
{
    public partial string? Name { get; set; }
}
