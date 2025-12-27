using System.Collections.Concurrent;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Authorization;
using HomeBlaze.Authorization.Configuration;
using HomeBlaze.Authorization.Data;
using HomeBlaze.Authorization.Roles;
using HomeBlaze.Authorization.Services;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Parent;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// Tests for AuthorizationResolver permission resolution.
/// </summary>
public class AuthorizationResolverTests
{
    private readonly AuthorizationOptions _options;
    private readonly AuthorizationResolver _resolver;

    public AuthorizationResolverTests()
    {
        _options = new AuthorizationOptions();
        _resolver = new AuthorizationResolver(_options);
    }

    [Fact]
    public void ResolvePropertyRoles_NoOverrides_ReturnsDefaultRoles()
    {
        // Arrange
        var subject = CreateMockSubject();
        var property = new PropertyReference(subject, "Temperature");

        // Act
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert - State.Read defaults to Anonymous (allows unauthenticated access)
        Assert.Contains(DefaultRoles.Anonymous, roles);
    }

    [Fact]
    public void ResolvePropertyRoles_StateWrite_ReturnsOperatorRole()
    {
        // Arrange
        var subject = CreateMockSubject();
        var property = new PropertyReference(subject, "Temperature");

        // Act
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Write);

        // Assert
        Assert.Contains(DefaultRoles.Operator, roles);
    }

    [Fact]
    public void ResolvePropertyRoles_ConfigurationRead_ReturnsUserRole()
    {
        // Arrange
        var subject = CreateMockSubject();
        var property = new PropertyReference(subject, "Settings");

        // Act
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.Configuration, AuthorizationAction.Read);

        // Assert
        Assert.Contains(DefaultRoles.User, roles);
    }

    [Fact]
    public void ResolvePropertyRoles_ConfigurationWrite_ReturnsSupervisorRole()
    {
        // Arrange
        var subject = CreateMockSubject();
        var property = new PropertyReference(subject, "Settings");

        // Act
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.Configuration, AuthorizationAction.Write);

        // Assert
        Assert.Contains(DefaultRoles.Supervisor, roles);
    }

    [Fact]
    public void ResolveMethodRoles_Query_ReturnsUserRole()
    {
        // Arrange
        var subject = CreateMockSubject();

        // Act
        var roles = _resolver.ResolveMethodRoles(subject, AuthorizationEntity.Query, "GetStatus");

        // Assert
        Assert.Contains(DefaultRoles.User, roles);
    }

    [Fact]
    public void ResolveMethodRoles_Operation_ReturnsOperatorRole()
    {
        // Arrange
        var subject = CreateMockSubject();

        // Act
        var roles = _resolver.ResolveMethodRoles(subject, AuthorizationEntity.Operation, "TurnOn");

        // Assert
        Assert.Contains(DefaultRoles.Operator, roles);
    }

    [Fact]
    public void ResolvePropertyRoles_CachesResults()
    {
        // Arrange
        var subject = CreateMockSubject();
        var property = new PropertyReference(subject, "Temperature");

        // Act - call twice
        var roles1 = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);
        var roles2 = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert - should be same reference (cached)
        Assert.Same(roles1, roles2);
    }

    [Fact]
    public void InvalidateCache_ClearsCache()
    {
        // Arrange
        var subject = CreateMockSubject();
        var property = new PropertyReference(subject, "Temperature");

        // First call caches
        var roles1 = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Act
        _resolver.InvalidateCache(subject);

        // Get again
        var roles2 = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert - should be different reference (cache was cleared)
        Assert.NotSame(roles1, roles2);
        // But same content
        Assert.Equal(roles1, roles2);
    }

    [Fact]
    public void ResolvePropertyRoles_CustomDefaults_ReturnsCustomRoles()
    {
        // Arrange
        var customOptions = new AuthorizationOptions
        {
            DefaultPermissionRoles =
            {
                [(AuthorizationEntity.State, AuthorizationAction.Read)] = ["CustomReader"]
            }
        };
        var resolver = new AuthorizationResolver(customOptions);
        var subject = CreateMockSubject();
        var property = new PropertyReference(subject, "Temperature");

        // Act
        var roles = resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert
        Assert.Contains("CustomReader", roles);
        Assert.DoesNotContain(DefaultRoles.Guest, roles);
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

    private static IInterceptorSubject CreateMockSubjectWithData(ConcurrentDictionary<(string?, string), object?> data)
    {
        var contextMock = new Mock<IInterceptorSubjectContext>();
        var subjectMock = new Mock<IInterceptorSubject>();
        subjectMock.Setup(s => s.Context).Returns(contextMock.Object);
        subjectMock.Setup(s => s.Data).Returns(data);
        subjectMock.Setup(s => s.Properties).Returns(new Dictionary<string, SubjectPropertyMetadata>());
        return subjectMock.Object;
    }

    #region Runtime Override Tests

    [Fact]
    public void ResolvePropertyRoles_PropertyOverride_ReturnsOverrideRoles()
    {
        // Arrange
        var data = new ConcurrentDictionary<(string?, string), object?>();
        var subject = CreateMockSubjectWithData(data);
        var property = new PropertyReference(subject, "Temperature");

        // Set property-level override
        var dataKey = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.State, AuthorizationAction.Read);
        data[("Temperature", dataKey)] = new AuthorizationOverride { Roles = ["CustomRole"] };

        // Act
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert
        Assert.Single(roles);
        Assert.Contains("CustomRole", roles);
    }

    [Fact]
    public void ResolvePropertyRoles_SubjectOverride_ReturnsOverrideRoles()
    {
        // Arrange
        var data = new ConcurrentDictionary<(string?, string), object?>();
        var subject = CreateMockSubjectWithData(data);
        var property = new PropertyReference(subject, "Temperature");

        // Set subject-level override (null property name)
        var dataKey = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.State, AuthorizationAction.Read);
        data[(null, dataKey)] = new AuthorizationOverride { Roles = ["SubjectRole"] };

        // Act
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert
        Assert.Single(roles);
        Assert.Contains("SubjectRole", roles);
    }

    [Fact]
    public void ResolvePropertyRoles_PropertyOverrideTakesPrecedenceOverSubject()
    {
        // Arrange
        var data = new ConcurrentDictionary<(string?, string), object?>();
        var subject = CreateMockSubjectWithData(data);
        var property = new PropertyReference(subject, "Temperature");
        var dataKey = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.State, AuthorizationAction.Read);

        // Set both property and subject overrides
        data[("Temperature", dataKey)] = new AuthorizationOverride { Roles = ["PropertyRole"] };
        data[(null, dataKey)] = new AuthorizationOverride { Roles = ["SubjectRole"] };

        // Act
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert - Property override wins
        Assert.Single(roles);
        Assert.Contains("PropertyRole", roles);
    }

    [Fact]
    public void ResolveMethodRoles_MethodOverride_ReturnsOverrideRoles()
    {
        // Arrange
        var data = new ConcurrentDictionary<(string?, string), object?>();
        var subject = CreateMockSubjectWithData(data);
        var dataKey = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.Operation, AuthorizationAction.Invoke);

        // Set method-level override
        data[("TurnOn", dataKey)] = new AuthorizationOverride { Roles = ["MethodRole"] };

        // Act
        var roles = _resolver.ResolveMethodRoles(subject, AuthorizationEntity.Operation, "TurnOn");

        // Assert
        Assert.Single(roles);
        Assert.Contains("MethodRole", roles);
    }

    #endregion

    #region Parent Traversal Tests

    [Fact]
    public void ResolvePropertyRoles_ParentWithInheritableOverride_InheritsRoles()
    {
        // Arrange - Set up parent-child relationship
        var parentData = new ConcurrentDictionary<(string?, string), object?>();
        var parent = CreateMockSubjectWithData(parentData);

        var childData = new ConcurrentDictionary<(string?, string), object?>();
        var child = CreateMockSubjectWithData(childData);

        // Add parent reference to child's Data
        var parents = (HashSet<SubjectParent>)childData.GetOrAdd((null, "Namotion.Parents"), _ => new HashSet<SubjectParent>())!;
        parents.Add(new SubjectParent(new PropertyReference(parent, "Children"), null));

        // Set inheritable override on parent
        var dataKey = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.State, AuthorizationAction.Read);
        parentData[(null, dataKey)] = new AuthorizationOverride { Inherit = true, Roles = ["ParentRole"] };

        var property = new PropertyReference(child, "Temperature");

        // Act
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert - Child inherits parent's roles
        Assert.Contains("ParentRole", roles);
    }

    [Fact]
    public void ResolvePropertyRoles_ParentWithNonInheritableOverride_FallsBackToDefaults()
    {
        // Arrange - Set up parent-child relationship
        var parentData = new ConcurrentDictionary<(string?, string), object?>();
        var parent = CreateMockSubjectWithData(parentData);

        var childData = new ConcurrentDictionary<(string?, string), object?>();
        var child = CreateMockSubjectWithData(childData);

        // Add parent reference
        var parents = (HashSet<SubjectParent>)childData.GetOrAdd((null, "Namotion.Parents"), _ => new HashSet<SubjectParent>())!;
        parents.Add(new SubjectParent(new PropertyReference(parent, "Children"), null));

        // Set non-inheritable override on parent (Inherit = false)
        var dataKey = AuthorizationDataProvider.GetDataKey(AuthorizationEntity.State, AuthorizationAction.Read);
        parentData[(null, dataKey)] = new AuthorizationOverride { Inherit = false, Roles = ["ParentRole"] };

        var property = new PropertyReference(child, "Temperature");

        // Act
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert - Child does NOT inherit parent's roles, falls back to defaults
        Assert.DoesNotContain("ParentRole", roles);
        Assert.Contains(DefaultRoles.Anonymous, roles); // Default for State.Read
    }

    #endregion

    #region Attribute-Based Authorization Tests

    [Fact]
    public void ResolvePropertyRoles_SubjectWithAuthorizeAttribute_ReturnsAttributeRoles()
    {
        // Arrange
        var subject = CreateAttributedSubject<SecureSubject>();
        var property = new PropertyReference(subject, "Temperature");

        // Act - SecureSubject has [SubjectAuthorize(State, Read, "SpecialReader")]
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert
        Assert.Single(roles);
        Assert.Contains("SpecialReader", roles);
    }

    [Fact]
    public void ResolvePropertyRoles_PropertyAuthorizeOverridesSubjectAuthorize()
    {
        // Arrange - SecureProperty has [SubjectPropertyAuthorize(Read, "AdminOnly")]
        var subject = CreateAttributedSubject<SubjectWithSecureProperty>();
        var property = new PropertyReference(subject, "SecureProperty");

        // Act
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert - Property attribute wins over subject attribute
        Assert.Single(roles);
        Assert.Contains("AdminOnly", roles);
    }

    [Fact]
    public void ResolvePropertyRoles_NonSecurePropertyUsesSubjectAttribute()
    {
        // Arrange - RegularProperty has no attribute, falls back to subject's attribute
        var subject = CreateAttributedSubject<SubjectWithSecureProperty>();
        var property = new PropertyReference(subject, "RegularProperty");

        // Act
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert - Subject attribute applies
        Assert.Single(roles);
        Assert.Contains("SubjectReader", roles);
    }

    [Fact]
    public void ResolveMethodRoles_MethodAuthorizeAttribute_ReturnsAttributeRoles()
    {
        // Arrange - Reset method has [SubjectMethodAuthorize("AdminOnly")]
        var subject = CreateAttributedSubject<SubjectWithSecureMethod>();

        // Act
        var roles = _resolver.ResolveMethodRoles(subject, AuthorizationEntity.Operation, "Reset");

        // Assert
        Assert.Single(roles);
        Assert.Contains("AdminOnly", roles);
    }

    [Fact]
    public void ResolveMethodRoles_NonSecureMethodUsesDefaults()
    {
        // Arrange - TurnOn has [Operation] but no [SubjectMethodAuthorize]
        var subject = CreateAttributedSubject<SubjectWithSecureMethod>();

        // Act
        var roles = _resolver.ResolveMethodRoles(subject, AuthorizationEntity.Operation, "TurnOn");

        // Assert - Falls back to default (Operator for Operation.Invoke)
        Assert.Contains(DefaultRoles.Operator, roles);
    }

    [Fact]
    public void ResolvePropertyRoles_SubjectAuthorizeForConfiguration_Works()
    {
        // Arrange - ConfigurationSubject has different roles for Configuration vs State
        var subject = CreateAttributedSubject<ConfigurationSubject>();
        var property = new PropertyReference(subject, "Settings");

        // Act - Get Configuration.Read roles
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.Configuration, AuthorizationAction.Read);

        // Assert
        Assert.Single(roles);
        Assert.Contains("ConfigReader", roles);
    }

    [Fact]
    public void ResolvePropertyRoles_SubjectAuthorizeForConfigurationWrite_Works()
    {
        // Arrange
        var subject = CreateAttributedSubject<ConfigurationSubject>();
        var property = new PropertyReference(subject, "Settings");

        // Act - Get Configuration.Write roles
        var roles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.Configuration, AuthorizationAction.Write);

        // Assert
        Assert.Single(roles);
        Assert.Contains("ConfigWriter", roles);
    }

    [Fact]
    public void ResolvePropertyRoles_MultipleSubjectAttributes_ReturnsCorrectRole()
    {
        // Arrange - MultiAttributeSubject has multiple [SubjectAuthorize] attributes
        var subject = CreateAttributedSubject<MultiAttributeSubject>();
        var property = new PropertyReference(subject, "Value");

        // Act - Get State.Write roles
        var stateWriteRoles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Write);

        // Assert - Should get StateWriter role
        Assert.Single(stateWriteRoles);
        Assert.Contains("StateWriter", stateWriteRoles);
    }

    [Fact]
    public void ResolvePropertyRoles_PropertyAuthorizeWriteOnly_ReadFallsToSubject()
    {
        // Arrange - WriteOnlySecure property has only Write authorization
        var subject = CreateAttributedSubject<SubjectWithWriteOnlySecure>();
        var property = new PropertyReference(subject, "WriteOnlySecure");

        // Act - Read should fall back to subject attribute (SubjectReader)
        var readRoles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Read);

        // Assert
        Assert.Single(readRoles);
        Assert.Contains("SubjectReader", readRoles);
    }

    [Fact]
    public void ResolvePropertyRoles_PropertyAuthorizeWriteOnly_WriteUsesPropertyAttr()
    {
        // Arrange
        var subject = CreateAttributedSubject<SubjectWithWriteOnlySecure>();
        var property = new PropertyReference(subject, "WriteOnlySecure");

        // Act - Write should use property attribute
        var writeRoles = _resolver.ResolvePropertyRoles(property, AuthorizationEntity.State, AuthorizationAction.Write);

        // Assert
        Assert.Single(writeRoles);
        Assert.Contains("WriteAdmin", writeRoles);
    }

    private static T CreateAttributedSubject<T>() where T : class, IInterceptorSubject
    {
        var contextMock = new Mock<IInterceptorSubjectContext>();
        var instance = (T)Activator.CreateInstance(typeof(T), contextMock.Object)!;
        return instance;
    }

    #endregion
}

#region Test Subjects for Attribute-Based Authorization

/// <summary>
/// Subject with [SubjectAuthorize] attribute for State.Read.
/// </summary>
[SubjectAuthorize(AuthorizationEntity.State, AuthorizationAction.Read, "SpecialReader")]
public class SecureSubject : IInterceptorSubject
{
    public SecureSubject(IInterceptorSubjectContext context)
    {
        Context = context;
    }

    public object SyncRoot { get; } = new();
    public IInterceptorSubjectContext Context { get; }
    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } = new Dictionary<string, SubjectPropertyMetadata>();

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) { }

    public double Temperature { get; set; }
}

/// <summary>
/// Subject with both subject-level and property-level authorization.
/// </summary>
[SubjectAuthorize(AuthorizationEntity.State, AuthorizationAction.Read, "SubjectReader")]
public class SubjectWithSecureProperty : IInterceptorSubject
{
    public SubjectWithSecureProperty(IInterceptorSubjectContext context)
    {
        Context = context;
    }

    public object SyncRoot { get; } = new();
    public IInterceptorSubjectContext Context { get; }
    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } = new Dictionary<string, SubjectPropertyMetadata>();

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) { }

    [SubjectPropertyAuthorize(AuthorizationAction.Read, "AdminOnly")]
    public string SecureProperty { get; set; } = "";

    public double RegularProperty { get; set; }
}

/// <summary>
/// Subject with method-level authorization.
/// </summary>
public class SubjectWithSecureMethod : IInterceptorSubject
{
    public SubjectWithSecureMethod(IInterceptorSubjectContext context)
    {
        Context = context;
    }

    public object SyncRoot { get; } = new();
    public IInterceptorSubjectContext Context { get; }
    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } = new Dictionary<string, SubjectPropertyMetadata>();

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) { }

    [Operation]
    [SubjectMethodAuthorize("AdminOnly")]
    public void Reset() { }

    [Operation]
    public void TurnOn() { }
}

/// <summary>
/// Subject with Configuration entity authorization.
/// </summary>
[SubjectAuthorize(AuthorizationEntity.Configuration, AuthorizationAction.Read, "ConfigReader")]
[SubjectAuthorize(AuthorizationEntity.Configuration, AuthorizationAction.Write, "ConfigWriter")]
public class ConfigurationSubject : IInterceptorSubject
{
    public ConfigurationSubject(IInterceptorSubjectContext context)
    {
        Context = context;
    }

    public object SyncRoot { get; } = new();
    public IInterceptorSubjectContext Context { get; }
    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } = new Dictionary<string, SubjectPropertyMetadata>();

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) { }

    public string Settings { get; set; } = "";
}

/// <summary>
/// Subject with multiple SubjectAuthorize attributes for different entity/action combos.
/// </summary>
[SubjectAuthorize(AuthorizationEntity.State, AuthorizationAction.Read, "StateReader")]
[SubjectAuthorize(AuthorizationEntity.State, AuthorizationAction.Write, "StateWriter")]
[SubjectAuthorize(AuthorizationEntity.Configuration, AuthorizationAction.Read, "ConfigReader")]
public class MultiAttributeSubject : IInterceptorSubject
{
    public MultiAttributeSubject(IInterceptorSubjectContext context)
    {
        Context = context;
    }

    public object SyncRoot { get; } = new();
    public IInterceptorSubjectContext Context { get; }
    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } = new Dictionary<string, SubjectPropertyMetadata>();

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) { }

    public double Value { get; set; }
}

/// <summary>
/// Subject with property that only has Write authorization (Read falls back).
/// </summary>
[SubjectAuthorize(AuthorizationEntity.State, AuthorizationAction.Read, "SubjectReader")]
public class SubjectWithWriteOnlySecure : IInterceptorSubject
{
    public SubjectWithWriteOnlySecure(IInterceptorSubjectContext context)
    {
        Context = context;
    }

    public object SyncRoot { get; } = new();
    public IInterceptorSubjectContext Context { get; }
    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } = new Dictionary<string, SubjectPropertyMetadata>();

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) { }

    [SubjectPropertyAuthorize(AuthorizationAction.Write, "WriteAdmin")]
    public string WriteOnlySecure { get; set; } = "";
}

#endregion
