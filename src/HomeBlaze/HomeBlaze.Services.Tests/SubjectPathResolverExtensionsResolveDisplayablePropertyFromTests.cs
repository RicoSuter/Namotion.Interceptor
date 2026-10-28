using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Attributes;
using HomeBlaze.Services.Tests.Models;

namespace HomeBlaze.Services.Tests;

public class SubjectPathResolverExtensionsResolveDisplayablePropertyFromTests : SubjectPathResolverTestBase
{
    [Fact]
    public void WhenPathIsAPropertyName_ThenThePropertyOfTheSubjectIsResolved()
    {
        // Arrange
        var subject = new TestContainer(Context) { Name = "Subject" };

        // Act
        var property = Resolver.ResolveDisplayablePropertyFrom(nameof(TestContainer.Name), PathStyle.Canonical, subject);

        // Assert
        Assert.NotNull(property);
        Assert.Same(subject, property.Subject);
        Assert.Equal(nameof(TestContainer.Name), property.Name);
    }

    [Fact]
    public void WhenPathLeadsThroughAMember_ThenThePropertyOfTheMemberIsResolved()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var subject = new TestContainer(Context) { Name = "Subject", Child = child };

        // Act
        var property = Resolver.ResolveDisplayablePropertyFrom("Child/Name", PathStyle.Canonical, subject);

        // Assert
        Assert.NotNull(property);
        Assert.Same(child, property.Subject);
        Assert.Equal("Child", property.GetValue());
    }

    [Fact]
    public void WhenPropertyIsNotDisplayable_ThenNullIsResolved()
    {
        // Arrange
        var subject = new SecretConfigTestSubject(Context) { SecretKey = "s3cr3t" };

        // Act
        var plain = Resolver.ResolveDisplayablePropertyFrom(nameof(SecretConfigTestSubject.PlainText), PathStyle.Canonical, subject);
        var excluded = Resolver.ResolveDisplayablePropertyFrom(nameof(SecretConfigTestSubject.RawSource), PathStyle.Canonical, subject);

        // Assert
        Assert.Null(plain);
        Assert.Null(excluded);
    }

    [Theory]
    [InlineData("Child/")]
    [InlineData("")]
    [InlineData("/")]
    public void WhenPathHasNoPropertyName_ThenNullIsResolved(string path)
    {
        // Arrange
        var subject = new TestContainer(Context) { Name = "Subject", Child = new TestContainer(Context) { Name = "Child" } };

        // Act
        var property = Resolver.ResolveDisplayablePropertyFrom(path, PathStyle.Canonical, subject);

        // Assert
        Assert.Null(property);
    }

    [Fact]
    public void WhenPathLeadsNowhere_ThenNullIsResolved()
    {
        // Arrange
        var subject = new TestContainer(Context) { Name = "Subject" };

        // Act
        var property = Resolver.ResolveDisplayablePropertyFrom("Nothing/Name", PathStyle.Canonical, subject);

        // Assert
        Assert.Null(property);
    }
}
