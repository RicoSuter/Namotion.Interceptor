using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class RegisteredSubjectAttributeTests
{
    [Fact]
    public void WhenAttributeRegistered_ThenIsRegisteredSubjectAttribute()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var maxLength = registered.TryGetMember("FirstName_MaxLength");

        // Assert
        Assert.IsType<RegisteredSubjectAttribute>(maxLength);
        Assert.IsAssignableFrom<RegisteredSubjectProperty>(maxLength);
        Assert.IsAssignableFrom<RegisteredSubjectMember>(maxLength);
    }

    [Fact]
    public void WhenAttributeRegistered_ThenGetAttributedMemberReturnsParentProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var maxLength = (RegisteredSubjectAttribute)registered.TryGetMember("FirstName_MaxLength")!;

        // Assert
        var parent = maxLength.GetAttributedMember();
        Assert.Equal("FirstName", parent.Name);
        Assert.IsType<RegisteredSubjectProperty>(parent);
    }

    [Fact]
    public void WhenAttributeRegistered_ThenBrowseNameIsAttributeName()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var maxLength = (RegisteredSubjectAttribute)registered.TryGetMember("FirstName_MaxLength")!;

        // Assert
        Assert.Equal("MaxLength", maxLength.BrowseName);
        Assert.Equal("MaxLength", maxLength.AttributeName);
    }

    [Fact]
    public void WhenPropertyHasAttributes_ThenAttributesArrayIsPopulated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var firstName = registered.TryGetProperty("FirstName")!;

        // Assert
        Assert.Single(firstName.Attributes);
        Assert.Equal("MaxLength", firstName.Attributes[0].AttributeName);
    }

    [Fact]
    public void WhenTryGetAttributeCalled_ThenReturnsCorrectAttribute()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var maxLength = firstName.TryGetAttribute("MaxLength");

        // Assert
        Assert.NotNull(maxLength);
        Assert.Equal("MaxLength", maxLength.AttributeName);
    }

    [Fact]
    public void WhenTryGetAttributeCalledWithWrongName_ThenReturnsNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var result = firstName.TryGetAttribute("NonExistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenNestedAttributeExists_ThenItHasCorrectParent()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var unit = (RegisteredSubjectAttribute)registered.TryGetMember("FirstName_MaxLength_Unit")!;

        // Assert
        var parent = unit.GetAttributedMember();
        Assert.Equal("FirstName_MaxLength", parent.Name);
        Assert.IsType<RegisteredSubjectAttribute>(parent);
    }

    [Fact]
    public void WhenAttributesCached_ThenSubsequentCallsReturnSameArray()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var attributes1 = firstName.Attributes;
        var attributes2 = firstName.Attributes;

        // Assert
        Assert.Same(attributes1, attributes2);
    }

    [Fact]
    public void WhenPropertiesAccessed_ThenAttributesAreExcluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);

        // Act
        var registered = person.TryGetRegisteredSubject()!;

        // Assert
        Assert.DoesNotContain(registered.Properties, p => p is RegisteredSubjectAttribute);
        Assert.DoesNotContain(registered.Properties, p => p.Name == "FirstName_MaxLength");
    }

    [Fact]
    public void WhenMembersAccessed_ThenAttributesAreIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);

        // Act
        var registered = person.TryGetRegisteredSubject()!;

        // Assert
        Assert.True(registered.Members.ContainsKey("FirstName_MaxLength"));
        Assert.True(registered.Members.ContainsKey("FirstName_MaxLength_Unit"));
    }

    [Fact]
    public void WhenDynamicAttributeAddedToProperty_ThenAppearsInAttributes()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        firstName.AddAttribute("Description", typeof(string),
            _ => "A person's first name", null);

        // Assert
        var description = firstName.TryGetAttribute("Description");
        Assert.NotNull(description);
        Assert.Equal("A person's first name", description.GetValue());
    }

    [Fact]
    public void WhenAddingAttributesConcurrently_ThenAllAttributesAreRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;
        var attributeCount = 50;

        // Act
        Parallel.For(0, attributeCount, i =>
        {
            firstName.AddAttribute($"Attr{i}", typeof(int), _ => i, null);
        });

        // Assert
        // Original "MaxLength" + 50 dynamic attributes
        Assert.Equal(1 + attributeCount, firstName.Attributes.Length);
    }
}
