using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Host.Services.Display;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Host.Services.Tests.Display;

public class SubjectDisplayExtensionsTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
    }

    private static (RegisteredSubjectProperty Property, SubjectPropertyChild Child) GetSingleChild(
        ChildLabelParent parent, string propertyName)
    {
        var property = parent.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;
        return (property, Assert.Single(property.Children));
    }

    [Fact]
    public void WhenReferenceHasStateTitle_ThenLabelIsStateTitle()
    {
        // Arrange
        var parent = new ChildLabelParent(CreateContext())
        {
            TitledReference = new ChildLabelItem { Title = "Subject title" }
        };
        var (property, child) = GetSingleChild(parent, nameof(ChildLabelParent.TitledReference));

        // Act
        var label = property.GetChildDisplayName(child);

        // Assert
        Assert.Equal("Operating status", label);
    }

    [Fact]
    public void WhenReferenceHasNoStateTitle_ThenLabelIsPropertyName()
    {
        // Arrange
        var parent = new ChildLabelParent(CreateContext())
        {
            Reference = new ChildLabelItem { Title = "Subject title" }
        };
        var (property, child) = GetSingleChild(parent, nameof(ChildLabelParent.Reference));

        // Act
        var label = property.GetChildDisplayName(child);

        // Assert
        Assert.Equal(nameof(ChildLabelParent.Reference), label);
    }

    [Fact]
    public void WhenCollectionItemHasTitle_ThenLabelIsSubjectTitle()
    {
        // Arrange
        var parent = new ChildLabelParent(CreateContext())
        {
            Items = [new ChildLabelItem { Title = "First" }]
        };
        var (property, child) = GetSingleChild(parent, nameof(ChildLabelParent.Items));

        // Act
        var label = property.GetChildDisplayName(child);

        // Assert
        Assert.Equal("First", label);
    }

    [Fact]
    public void WhenCollectionItemHasNoTitle_ThenLabelIsIndex()
    {
        // Arrange
        var parent = new ChildLabelParent(CreateContext())
        {
            Items = [new ChildLabelItem { Title = "First" }, new ChildLabelItem()]
        };
        var property = parent.TryGetRegisteredSubject()!.TryGetProperty(nameof(ChildLabelParent.Items))!;
        var child = property.Children.Single(child => Equals(child.Index, 1));

        // Act
        var label = property.GetChildDisplayName(child);

        // Assert
        Assert.Equal("1", label);
    }

    [Fact]
    public void WhenDictionaryItemHasNoTitle_ThenLabelIsKey()
    {
        // Arrange
        var parent = new ChildLabelParent(CreateContext())
        {
            ItemsByKey = new Dictionary<string, ChildLabelItem> { ["kitchen"] = new() }
        };
        var (property, child) = GetSingleChild(parent, nameof(ChildLabelParent.ItemsByKey));

        // Act
        var label = property.GetChildDisplayName(child);

        // Assert
        Assert.Equal("kitchen", label);
    }

    [Fact]
    public void WhenDictionaryItemHasNoTitleAndEmptyKey_ThenLabelIsShortTypeName()
    {
        // Arrange
        var parent = new ChildLabelParent(CreateContext())
        {
            ItemsByKey = new Dictionary<string, ChildLabelItem> { [""] = new() }
        };
        var (property, child) = GetSingleChild(parent, nameof(ChildLabelParent.ItemsByKey));

        // Act
        var label = property.GetChildDisplayName(child);

        // Assert
        Assert.Equal(nameof(ChildLabelItem), label);
    }

    [Theory]
    [InlineData("Living room", "living-room", "Living room (living-room)")]
    [InlineData("Living room", 2, "Living room (2)")]
    [InlineData("kitchen", "kitchen", "kitchen")]
    [InlineData("Reference", null, "Reference")]
    [InlineData("", "kitchen", "kitchen")]
    public void WhenItemHasKey_ThenIdentifyingDisplayNameIncludesItOnce(string displayName, object? index, string expected)
    {
        // Act
        var label = SubjectDisplayExtensions.GetIdentifyingDisplayName(displayName, index);

        // Assert
        Assert.Equal(expected, label);
    }
}

[InterceptorSubject]
public partial class ChildLabelParent
{
    [State(Title = "Operating status")]
    public partial ChildLabelItem? TitledReference { get; set; }

    [State]
    public partial ChildLabelItem? Reference { get; set; }

    public partial ChildLabelItem[] Items { get; set; }

    public partial Dictionary<string, ChildLabelItem> ItemsByKey { get; set; }

    public ChildLabelParent()
    {
        Items = [];
        ItemsByKey = new();
    }
}

[InterceptorSubject]
public partial class ChildLabelItem : ITitleProvider
{
    public partial string? Title { get; set; }
}
