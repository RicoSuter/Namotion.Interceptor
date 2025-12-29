using System.Collections.Concurrent;
using System.Text.Json;
using HomeBlaze.Abstractions.Authorization;
using HomeBlaze.Authorization.Data;
using HomeBlaze.Authorization.Services;
using Moq;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// Tests for AuthorizationDataProvider serialization and data access.
/// </summary>
public class AuthorizationDataProviderTests
{
    private readonly AuthorizationDataProvider _provider;

    public AuthorizationDataProviderTests()
    {
        _provider = new AuthorizationDataProvider();
    }

    [Fact]
    public void GetDataKey_ReturnsCorrectFormat()
    {
        // Act
        var key = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert
        Assert.Equal("HomeBlaze.Authorization:State:Read", key);
    }

    [Theory]
    [InlineData(AuthorizationEntity.State, AuthorizationAction.Read, "HomeBlaze.Authorization:State:Read")]
    [InlineData(AuthorizationEntity.State, AuthorizationAction.Write, "HomeBlaze.Authorization:State:Write")]
    [InlineData(AuthorizationEntity.Configuration, AuthorizationAction.Read, "HomeBlaze.Authorization:Configuration:Read")]
    [InlineData(AuthorizationEntity.Configuration, AuthorizationAction.Write, "HomeBlaze.Authorization:Configuration:Write")]
    [InlineData(AuthorizationEntity.Query, AuthorizationAction.Invoke, "HomeBlaze.Authorization:Query:Invoke")]
    [InlineData(AuthorizationEntity.Operation, AuthorizationAction.Invoke, "HomeBlaze.Authorization:Operation:Invoke")]
    public void GetDataKey_AllEntityActionCombinations(AuthorizationEntity entity, AuthorizationAction action, string expected)
    {
        // Act
        var key = AuthorizationDataProvider.GetDataKey(entity, action);

        // Assert
        Assert.Equal(expected, key);
    }

    [Fact]
    public void GetAdditionalProperties_NullSubject_ReturnsNull()
    {
        // Act
        var result = _provider.GetAdditionalProperties(null!);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetAdditionalProperties_NoOverrides_ReturnsNull()
    {
        // Arrange
        var subject = CreateMockSubject();

        // Act
        var result = _provider.GetAdditionalProperties(subject);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetAdditionalProperties_WithSubjectOverride_ReturnsSerializedOverride()
    {
        // Arrange
        var subject = CreateMockSubject();
        var authOverride = new AuthorizationOverride { Inherit = false, Roles = ["Admin", "User"] };
        var dataKey = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.State, AuthorizationAction.Read);
        subject.Data[(null, dataKey)] = authOverride;

        // Act
        var result = _provider.GetAdditionalProperties(subject);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.ContainsKey("authorization"));
    }

    [Fact]
    public void GetAdditionalProperties_WithPropertyOverride_ReturnsSerializedOverride()
    {
        // Arrange
        var subject = CreateMockSubject();
        var authOverride = new AuthorizationOverride { Inherit = true, Roles = ["Operator"] };
        var dataKey = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.Configuration, AuthorizationAction.Write);
        subject.Data[("Temperature", dataKey)] = authOverride;

        // Act
        var result = _provider.GetAdditionalProperties(subject);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.ContainsKey("authorization"));
    }

    [Fact]
    public void SetAdditionalProperties_NullSubject_DoesNotThrow()
    {
        // Arrange
        var properties = new Dictionary<string, JsonElement>();

        // Act & Assert - should not throw
        _provider.SetAdditionalProperties(null!, properties);
    }

    [Fact]
    public void SetAdditionalProperties_NullProperties_DoesNotThrow()
    {
        // Arrange
        var subject = CreateMockSubject();

        // Act & Assert - should not throw
        _provider.SetAdditionalProperties(subject, null!);
    }

    [Fact]
    public void SetAdditionalProperties_NoAuthorizationKey_DoesNothing()
    {
        // Arrange
        var subject = CreateMockSubject();
        var properties = new Dictionary<string, JsonElement>
        {
            ["otherProperty"] = JsonSerializer.SerializeToElement("value")
        };

        // Act
        _provider.SetAdditionalProperties(subject, properties);

        // Assert
        Assert.Empty(subject.Data);
    }

    [Fact]
    public void SetAdditionalProperties_WithAuthorizationData_SetsOverrides()
    {
        // Arrange
        var subject = CreateMockSubject();
        var authData = new Dictionary<string, Dictionary<string, AuthorizationOverride>>
        {
            [""] = new Dictionary<string, AuthorizationOverride>
            {
                ["State:Read"] = new AuthorizationOverride { Inherit = false, Roles = ["Admin"] }
            }
        };
        var properties = new Dictionary<string, JsonElement>
        {
            ["authorization"] = JsonSerializer.SerializeToElement(authData)
        };

        // Act
        _provider.SetAdditionalProperties(subject, properties);

        // Assert
        var dataKey = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.State, AuthorizationAction.Read);
        Assert.True(subject.Data.ContainsKey((null, dataKey)));
        var storedOverride = subject.Data[(null, dataKey)] as AuthorizationOverride;
        Assert.NotNull(storedOverride);
        Assert.False(storedOverride.Inherit);
        Assert.Contains("Admin", storedOverride.Roles);
    }

    [Fact]
    public void SetAdditionalProperties_WithPropertyOverride_SetsCorrectKey()
    {
        // Arrange
        var subject = CreateMockSubject();
        var authData = new Dictionary<string, Dictionary<string, AuthorizationOverride>>
        {
            ["Temperature"] = new Dictionary<string, AuthorizationOverride>
            {
                ["Configuration:Write"] = new AuthorizationOverride { Inherit = true, Roles = ["Supervisor"] }
            }
        };
        var properties = new Dictionary<string, JsonElement>
        {
            ["authorization"] = JsonSerializer.SerializeToElement(authData)
        };

        // Act
        _provider.SetAdditionalProperties(subject, properties);

        // Assert
        var dataKey = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.Configuration, AuthorizationAction.Write);
        Assert.True(subject.Data.ContainsKey(("Temperature", dataKey)));
        var storedOverride = subject.Data[("Temperature", dataKey)] as AuthorizationOverride;
        Assert.NotNull(storedOverride);
        Assert.True(storedOverride.Inherit);
        Assert.Contains("Supervisor", storedOverride.Roles);
    }

    [Fact]
    public void TryGetOverride_NoOverride_ReturnsFalse()
    {
        // Arrange
        var subject = CreateMockSubject();

        // Act
        var result = AuthorizationDataProvider.TryGetOverride(
            subject, null, AuthorizationEntity.State, AuthorizationAction.Read, out var authOverride);

        // Assert
        Assert.False(result);
        Assert.Null(authOverride);
    }

    [Fact]
    public void TryGetOverride_WithOverride_ReturnsTrueAndOverride()
    {
        // Arrange
        var subject = CreateMockSubject();
        var expectedOverride = new AuthorizationOverride { Inherit = false, Roles = ["Admin"] };
        AuthorizationDataProvider.SetOverride(subject, null, AuthorizationEntity.State, AuthorizationAction.Read, expectedOverride);

        // Act
        var result = AuthorizationDataProvider.TryGetOverride(
            subject, null, AuthorizationEntity.State, AuthorizationAction.Read, out var authOverride);

        // Assert
        Assert.True(result);
        Assert.NotNull(authOverride);
        Assert.Equal(expectedOverride.Inherit, authOverride.Inherit);
        Assert.Equal(expectedOverride.Roles, authOverride.Roles);
    }

    [Fact]
    public void TryGetOverride_PropertyLevel_ReturnsCorrectOverride()
    {
        // Arrange
        var subject = CreateMockSubject();
        var propertyOverride = new AuthorizationOverride { Inherit = true, Roles = ["User"] };
        AuthorizationDataProvider.SetOverride(subject, "Temperature", AuthorizationEntity.State, AuthorizationAction.Write, propertyOverride);

        // Act
        var result = AuthorizationDataProvider.TryGetOverride(
            subject, "Temperature", AuthorizationEntity.State, AuthorizationAction.Write, out var authOverride);

        // Assert
        Assert.True(result);
        Assert.NotNull(authOverride);
        Assert.Equal(propertyOverride.Roles, authOverride.Roles);
    }

    [Fact]
    public void SetOverride_SetsValueInSubjectData()
    {
        // Arrange
        var subject = CreateMockSubject();
        var authOverride = new AuthorizationOverride { Inherit = false, Roles = ["Admin", "Operator"] };

        // Act
        AuthorizationDataProvider.SetOverride(subject, null, AuthorizationEntity.Operation, AuthorizationAction.Invoke, authOverride);

        // Assert
        var dataKey = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.Operation, AuthorizationAction.Invoke);
        Assert.True(subject.Data.ContainsKey((null, dataKey)));
        Assert.Same(authOverride, subject.Data[(null, dataKey)]);
    }

    [Fact]
    public void RemoveOverride_RemovesValueFromSubjectData()
    {
        // Arrange
        var subject = CreateMockSubject();
        var authOverride = new AuthorizationOverride { Inherit = false, Roles = ["Admin"] };
        AuthorizationDataProvider.SetOverride(subject, null, AuthorizationEntity.State, AuthorizationAction.Read, authOverride);

        // Verify it's set
        var dataKey = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.State, AuthorizationAction.Read);
        Assert.True(subject.Data.ContainsKey((null, dataKey)));

        // Act
        AuthorizationDataProvider.RemoveOverride(subject, null, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert
        Assert.False(subject.Data.ContainsKey((null, dataKey)));
    }

    [Fact]
    public void RemoveOverride_NonExistentOverride_DoesNotThrow()
    {
        // Arrange
        var subject = CreateMockSubject();

        // Act & Assert - should not throw
        AuthorizationDataProvider.RemoveOverride(subject, null, AuthorizationEntity.State, AuthorizationAction.Read);
    }

    [Fact]
    public void RoundTrip_SubjectOverride_PreservesData()
    {
        // Arrange
        var subject = CreateMockSubject();
        var originalOverride = new AuthorizationOverride { Inherit = false, Roles = ["Admin", "User"] };
        AuthorizationDataProvider.SetOverride(subject, null, AuthorizationEntity.State, AuthorizationAction.Read, originalOverride);

        // Act - serialize
        var serialized = _provider.GetAdditionalProperties(subject);
        Assert.NotNull(serialized);

        // Create new subject and deserialize
        var newSubject = CreateMockSubject();
        var jsonElement = JsonSerializer.SerializeToElement(serialized["authorization"]);
        var properties = new Dictionary<string, JsonElement> { ["authorization"] = jsonElement };
        _provider.SetAdditionalProperties(newSubject, properties);

        // Assert
        var result = AuthorizationDataProvider.TryGetOverride(
            newSubject, null, AuthorizationEntity.State, AuthorizationAction.Read, out var restoredOverride);
        Assert.True(result);
        Assert.NotNull(restoredOverride);
        Assert.Equal(originalOverride.Inherit, restoredOverride.Inherit);
        Assert.Equal(originalOverride.Roles, restoredOverride.Roles);
    }

    [Fact]
    public void GetAdditionalProperties_JsonOutput_ContainsRoles()
    {
        // Arrange
        var subject = CreateMockSubject();
        var authOverride = new AuthorizationOverride { Inherit = true, Roles = ["Admin", "Supervisor"] };
        AuthorizationDataProvider.SetOverride(subject, null, AuthorizationEntity.State, AuthorizationAction.Read, authOverride);

        // Act
        var serialized = _provider.GetAdditionalProperties(subject);
        Assert.NotNull(serialized);

        // Serialize to actual JSON string
        var jsonString = JsonSerializer.Serialize(serialized, new JsonSerializerOptions { WriteIndented = true });

        // Assert - JSON should contain the roles
        Assert.Contains("Admin", jsonString);
        Assert.Contains("Supervisor", jsonString);
        Assert.Contains("Inherit", jsonString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Roles", jsonString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetAdditionalProperties_JsonOutput_CorrectStructure()
    {
        // Arrange
        var subject = CreateMockSubject();
        var authOverride = new AuthorizationOverride { Inherit = false, Roles = ["Operator"] };
        AuthorizationDataProvider.SetOverride(subject, null, AuthorizationEntity.State, AuthorizationAction.Write, authOverride);

        // Act
        var serialized = _provider.GetAdditionalProperties(subject);
        Assert.NotNull(serialized);

        // Serialize and deserialize to verify structure
        var jsonString = JsonSerializer.Serialize(serialized);
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonString);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.ContainsKey("authorization"));

        var authElement = deserialized["authorization"];
        var authDict = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, AuthorizationOverride>>>(authElement.GetRawText());

        Assert.NotNull(authDict);
        Assert.True(authDict.ContainsKey("")); // Subject-level (empty path)
        Assert.True(authDict[""].ContainsKey("State:Write"));
        Assert.Equal(["Operator"], authDict[""]["State:Write"].Roles);
    }

    private static IInterceptorSubject CreateMockSubject()
    {
        var contextMock = new Mock<IInterceptorSubjectContext>();
        var subjectMock = new Mock<IInterceptorSubject>();
        subjectMock.Setup(s => s.Context).Returns(contextMock.Object);
        subjectMock.Setup(s => s.Data).Returns(new ConcurrentDictionary<(string?, string), object?>());
        subjectMock.Setup(s => s.Properties).Returns(new Dictionary<string, SubjectPropertyMetadata>());
        return subjectMock.Object;
    }
}
