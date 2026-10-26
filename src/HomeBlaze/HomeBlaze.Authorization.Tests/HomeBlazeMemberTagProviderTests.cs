using HomeBlaze.Abstractions;
using HomeBlaze.Authorization.Tests.Subjects;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public class HomeBlazeMemberTagProviderTests
{
    private readonly HomeBlazeMemberTagProvider _provider = new();

    [Fact]
    public void WhenAPropertyIsState_ThenItIsTaggedState()
    {
        // Arrange
        var member = GetMember(nameof(TagTestSubject.Temperature));

        // Act
        var tags = _provider.GetTags(member);

        // Assert
        AssertTags([MemberTags.State], tags);
    }

    [Fact]
    public void WhenAPropertyIsConfiguration_ThenItIsTaggedConfiguration()
    {
        // Arrange
        var member = GetMember(nameof(TagTestSubject.Host));

        // Act
        var tags = _provider.GetTags(member);

        // Assert
        AssertTags([MemberTags.Configuration], tags);
    }

    [Fact]
    public void WhenAPropertyIsSecretConfiguration_ThenItIsTaggedConfigurationAndSecret()
    {
        // Arrange
        var member = GetMember(nameof(TagTestSubject.Password));

        // Act
        var tags = _provider.GetTags(member);

        // Assert
        AssertTags([MemberTags.Configuration, MemberTags.Secret], tags);
    }

    [Fact]
    public void WhenAMemberIsAQuery_ThenItIsTaggedQuery()
    {
        // Arrange
        var member = GetMember("GetCount");

        // Act
        var tags = _provider.GetTags(member);

        // Assert
        AssertTags([MemberTags.Query], tags);
    }

    [Fact]
    public void WhenAMemberIsAnOperation_ThenItIsTaggedOperation()
    {
        // Arrange
        var member = GetMember("Start");

        // Act
        var tags = _provider.GetTags(member);

        // Assert
        AssertTags([MemberTags.Operation], tags);
    }

    [Fact]
    public void WhenAnOperationRequiresConfirmation_ThenItIsTaggedOperationAndRequiresConfirmation()
    {
        // Arrange
        var member = GetMember("Reset");

        // Act
        var tags = _provider.GetTags(member);

        // Assert
        AssertTags([MemberTags.Operation, MemberTags.RequiresConfirmation], tags);
    }

    [Fact]
    public void WhenAPropertyCanContainSubjects_ThenItIsTaggedSubjects()
    {
        // Arrange
        var member = GetMember(nameof(TagTestSubject.Child));

        // Act
        var tags = _provider.GetTags(member);

        // Assert
        AssertTags([MemberTags.Subjects], tags);
    }

    [Fact]
    public void WhenAMemberIsAnAttribute_ThenItIsTaggedAttribute()
    {
        // Arrange
        var member = GetMember(nameof(TagTestSubject.Temperature)).TryGetAttribute(KnownAttributes.Minimum)!;

        // Act
        var tags = _provider.GetTags(member);

        // Assert
        AssertTags([MemberTags.Attribute], tags);
    }

    [Fact]
    public void WhenAMemberIsTheMetadataOfASubject_ThenItIsTaggedMetadata()
    {
        // Arrange
        var subject = CreateSubject();
        var member = TestMetadata.AddSubjectMetadata(subject);

        // Act
        var tags = _provider.GetTags(member);

        // Assert
        AssertTags([MemberTags.Metadata], tags);
    }

    [Fact]
    public void WhenAMemberIsTheMetadataOfAProperty_ThenItIsTaggedMetadataAndAttribute()
    {
        // Arrange
        var subject = CreateSubject();
        var member = TestMetadata.AddPropertyMetadata(subject, nameof(TagTestSubject.Temperature));

        // Act
        var tags = _provider.GetTags(member);

        // Assert
        AssertTags([MemberTags.Attribute, MemberTags.Metadata], tags);
    }

    [Fact]
    public void WhenAPropertyHasNoMarker_ThenItHasNoTagsAndNotNull()
    {
        // Arrange
        var member = GetMember(nameof(TagTestSubject.Plain));

        // Act
        var tags = _provider.GetTags(member);

        // Assert
        Assert.NotNull(tags);
        Assert.Empty(tags);
    }

    [Fact]
    public void WhenTwoMembersHaveTheSameTags_ThenTheSameCachedArrayIsReturned()
    {
        // Arrange
        var first = CreateSubject().TryGetProperty(nameof(TagTestSubject.Password))!;
        var second = CreateSubject().TryGetProperty(nameof(TagTestSubject.Password))!;

        // Act
        var firstTags = _provider.GetTags(first);
        var secondTags = _provider.GetTags(second);

        // Assert
        Assert.IsType<string[]>(firstTags);
        Assert.Same(firstTags, secondTags);
    }

    private static RegisteredSubjectProperty GetMember(string name) =>
        CreateSubject().TryGetProperty(name) ?? throw new InvalidOperationException($"Member '{name}' is not registered.");

    private static RegisteredSubject CreateSubject() =>
        new TagTestSubject(TestContext.Create()).TryGetRegisteredSubject()!;

    private static void AssertTags(string[] expected, IReadOnlyCollection<string> actual) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
}
