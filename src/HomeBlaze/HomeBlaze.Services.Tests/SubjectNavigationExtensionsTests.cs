using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests;

public class SubjectNavigationExtensionsTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

    [Fact]
    public void WhenSubjectHasMetadata_ThenItsNavigationTitleAndIconAreFromIt()
    {
        // Arrange
        var subject = new NavigationTitledSubject(CreateContext()) { Title = "Motor 1", IconName = "Settings" };
        subject.SetSubjectMetadata(new SubjectMetadata("Pump", "Water", null, null));

        // Act
        var title = subject.GetNavigationTitle("Motor");
        var icon = subject.GetNavigationIconName();

        // Assert
        Assert.Equal("Pump", title);
        Assert.Equal("Water", icon);
    }

    [Fact]
    public void WhenSubjectHasNoMetadata_ThenItsOwnTitleAndIconApply()
    {
        // Arrange
        var subject = new NavigationTitledSubject(CreateContext()) { Title = "Motor 1", IconName = "Settings" };

        // Act
        var title = subject.GetNavigationTitle("Motor");
        var icon = subject.GetNavigationIconName();

        // Assert
        Assert.Equal("Motor 1", title);
        Assert.Equal("Settings", icon);
    }

    [Fact]
    public void WhenSubjectHasNoTitle_ThenTheKeyIsFormatted()
    {
        // Arrange
        var subject = new NavigationTitledSubject(CreateContext());

        // Act
        var title = subject.GetNavigationTitle("heat-pump.md");

        // Assert
        Assert.Equal("Heat Pump", title);
    }

    [Fact]
    public void WhenSubjectMetadataHasLocation_ThenLocationAndAlignmentAreFromIt()
    {
        // Arrange
        var subject = new NavigationTitledSubject(CreateContext());
        subject.SetSubjectMetadata(new SubjectMetadata(null, null, NavigationLocation.AppBar, AppBarAlignment.Right));

        // Act
        var location = subject.GetNavigationLocation();
        var alignment = subject.GetAppBarAlignment();

        // Assert
        Assert.Equal(NavigationLocation.AppBar, location);
        Assert.Equal(AppBarAlignment.Right, alignment);
    }

    [Fact]
    public void WhenFolderHasNavigationTitle_ThenItIsTheNavigationTitle()
    {
        // Arrange
        var folder = new NavigationTestFolder(CreateContext())
        {
            Metadata = new FolderMetadata("All devices", "Devices", null)
        };

        // Act
        var title = folder.GetNavigationTitle("devices");

        // Assert
        Assert.Equal("Devices", title);
    }
}

[InterceptorSubject]
public partial class NavigationTitledSubject : ITitleProvider, IIconProvider
{
    public partial string? Title { get; set; }

    public partial string? IconName { get; set; }
}

[InterceptorSubject]
public partial class NavigationTestFolder : IMetadataContainer, ITitleProvider
{
    public partial FolderMetadata? Metadata { get; set; }

    public partial ImmutableArray<string> ChildOrder { get; set; }

    public bool HasOwnMetadata => true;

    public string? Title => Metadata?.Title;

    public NavigationTestFolder()
    {
        ChildOrder = [];
    }

    public Task WriteMetadataAsync(FolderMetadata? metadata, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task WriteChildMetadataAsync(IInterceptorSubject child, SubjectMetadata? metadata, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task MoveChildAsync(string name, string targetName, CancellationToken cancellationToken) => Task.CompletedTask;
}
