using System.Collections.Concurrent;
using System.Security.Claims;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Authorization.Configuration;
using HomeBlaze.Authorization.Context;
using HomeBlaze.Authorization.Interceptors;
using HomeBlaze.Authorization.Roles;
using HomeBlaze.Authorization.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// Test subject with Configuration property for testing entity detection.
/// Implements IInterceptorSubject to allow direct use in interceptor tests.
/// </summary>
public class TestSubjectWithConfiguration : IInterceptorSubject
{
    private readonly Dictionary<string, SubjectPropertyMetadata> _properties = new();

    public TestSubjectWithConfiguration(IInterceptorSubjectContext context)
    {
        Context = context;
    }

    public object SyncRoot { get; } = new();
    public IInterceptorSubjectContext Context { get; }
    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => _properties;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
    {
        foreach (var property in properties)
        {
            _properties[property.Name] = property;
        }
    }

    [Configuration]
    public string ApiKey { get; set; } = "";

    public double Temperature { get; set; }
}

/// <summary>
/// Tests for AuthorizationInterceptor property access control.
/// </summary>
public class AuthorizationInterceptorTests : IDisposable
{
    private readonly AuthorizationInterceptor _interceptor;
    private readonly ServiceProvider _serviceProvider;

    public AuthorizationInterceptorTests()
    {
        var services = new ServiceCollection();
        var options = new AuthorizationOptions();
        services.AddSingleton(options);
        services.AddSingleton<IAuthorizationResolver, AuthorizationResolver>();
        _serviceProvider = services.BuildServiceProvider();

        _interceptor = new AuthorizationInterceptor();

        // Clear context before each test
        AuthorizationContext.Clear();
    }

    public void Dispose()
    {
        AuthorizationContext.Clear();
        _serviceProvider.Dispose();
    }

    [Fact]
    public void ReadProperty_WithAuthorizedUser_AllowsAccess()
    {
        // Arrange - Guest role expanded includes Anonymous (which is required for State.Read)
        SetupUserWithRoles(DefaultRoles.Guest, DefaultRoles.Anonymous);
        var subject = CreateMockSubject();
        var property = new PropertyReference(subject, "Temperature");
        var context = new PropertyReadContext(property);
        var value = 25.5;

        // Act
        var result = _interceptor.ReadProperty(ref context, (ref PropertyReadContext _) => value);

        // Assert
        Assert.Equal(value, result);
    }

    [Fact]
    public void WriteProperty_WithAuthorizedUser_AllowsAccess()
    {
        // Arrange
        SetupUserWithRoles(DefaultRoles.Operator);
        var subject = CreateMockSubject();
        var property = new PropertyReference(subject, "Temperature");
        var writeContext = new PropertyWriteContext<double>(property, 20.0, 25.0);
        var written = false;

        // Act
        _interceptor.WriteProperty(ref writeContext, (ref PropertyWriteContext<double> _) => written = true);

        // Assert
        Assert.True(written);
    }

    [Fact]
    public void WriteProperty_WithUnauthorizedUser_ThrowsUnauthorizedAccessException()
    {
        // Arrange - Guest cannot write State (requires Operator)
        SetupUserWithRoles(DefaultRoles.Guest);
        var subject = CreateMockSubject();
        var property = new PropertyReference(subject, "Temperature");
        var writeContext = new PropertyWriteContext<double>(property, 20.0, 25.0);

        // Act & Assert
        var exception = Assert.Throws<UnauthorizedAccessException>(() =>
            _interceptor.WriteProperty(ref writeContext, (ref PropertyWriteContext<double> _) => { }));

        Assert.Contains("Access denied", exception.Message);
        Assert.Contains("Temperature", exception.Message);
    }

    [Fact]
    public void ReadProperty_WithNoAuthResolver_AllowsAccess()
    {
        // Arrange - No IAuthorizationResolver in DI
        var subject = CreateMockSubjectWithoutResolver();
        var property = new PropertyReference(subject, "Temperature");
        var context = new PropertyReadContext(property);
        var value = 25.5;

        // Act - should gracefully degrade
        var result = _interceptor.ReadProperty(ref context, (ref PropertyReadContext _) => value);

        // Assert
        Assert.Equal(value, result);
    }

    private void SetupUserWithRoles(params string[] roles)
    {
        var identity = new ClaimsIdentity("TestAuth");
        var user = new ClaimsPrincipal(identity);
        AuthorizationContext.SetUser(user, roles);
    }

    private IInterceptorSubject CreateMockSubject()
    {
        var contextMock = new Mock<IInterceptorSubjectContext>();
        contextMock.Setup(c => c.TryGetService<IServiceProvider>()).Returns(_serviceProvider);

        var subjectMock = new Mock<IInterceptorSubject>();
        subjectMock.Setup(s => s.Context).Returns(contextMock.Object);
        subjectMock.Setup(s => s.Data).Returns(new ConcurrentDictionary<(string?, string), object?>());
        subjectMock.Setup(s => s.Properties).Returns(new Dictionary<string, SubjectPropertyMetadata>());

        return subjectMock.Object;
    }

    private IInterceptorSubject CreateMockSubjectWithoutResolver()
    {
        var contextMock = new Mock<IInterceptorSubjectContext>();
        contextMock.Setup(c => c.TryGetService<IServiceProvider>()).Returns((IServiceProvider?)null);

        var subjectMock = new Mock<IInterceptorSubject>();
        subjectMock.Setup(s => s.Context).Returns(contextMock.Object);
        subjectMock.Setup(s => s.Data).Returns(new ConcurrentDictionary<(string?, string), object?>());

        return subjectMock.Object;
    }

    private TestSubjectWithConfiguration CreateTestSubject()
    {
        var contextMock = new Mock<IInterceptorSubjectContext>();
        contextMock.Setup(c => c.TryGetService<IServiceProvider>()).Returns(_serviceProvider);
        return new TestSubjectWithConfiguration(contextMock.Object);
    }

    #region Configuration vs State Entity Detection Tests

    [Fact]
    public void ReadProperty_ConfigurationProperty_RequiresUserRole()
    {
        // Arrange - Configuration.Read requires User role by default
        // Guest should NOT be able to read Configuration
        SetupUserWithRoles(DefaultRoles.Guest, DefaultRoles.Anonymous);
        var subject = CreateTestSubject();
        var property = new PropertyReference(subject, "ApiKey"); // Has [Configuration] attribute
        var context = new PropertyReadContext(property);

        // Act - Guest cannot read Configuration, should return default
        var result = _interceptor.ReadProperty(ref context, (ref PropertyReadContext _) => "secret-key");

        // Assert - Should return default (null) because Guest lacks User role
        Assert.Null(result);
    }

    [Fact]
    public void ReadProperty_ConfigurationProperty_UserCanRead()
    {
        // Arrange - User role can read Configuration
        SetupUserWithRoles(DefaultRoles.User, DefaultRoles.Guest, DefaultRoles.Anonymous);
        var subject = CreateTestSubject();
        var property = new PropertyReference(subject, "ApiKey");
        var context = new PropertyReadContext(property);
        var value = "secret-key";

        // Act
        var result = _interceptor.ReadProperty(ref context, (ref PropertyReadContext _) => value);

        // Assert
        Assert.Equal(value, result);
    }

    [Fact]
    public void WriteProperty_ConfigurationProperty_RequiresSupervisorRole()
    {
        // Arrange - Operator cannot write Configuration (requires Supervisor)
        SetupUserWithRoles(DefaultRoles.Operator, DefaultRoles.User, DefaultRoles.Guest);
        var subject = CreateTestSubject();
        var property = new PropertyReference(subject, "ApiKey");
        var writeContext = new PropertyWriteContext<string>(property, "", "new-key");

        // Act & Assert - Operator cannot write Configuration
        Assert.Throws<UnauthorizedAccessException>(() =>
            _interceptor.WriteProperty(ref writeContext, (ref PropertyWriteContext<string> _) => { }));
    }

    [Fact]
    public void WriteProperty_ConfigurationProperty_SupervisorCanWrite()
    {
        // Arrange - Supervisor can write Configuration
        SetupUserWithRoles(DefaultRoles.Supervisor, DefaultRoles.Operator, DefaultRoles.User);
        var subject = CreateTestSubject();
        var property = new PropertyReference(subject, "ApiKey");
        var writeContext = new PropertyWriteContext<string>(property, "", "new-key");
        var written = false;

        // Act
        _interceptor.WriteProperty(ref writeContext, (ref PropertyWriteContext<string> _) => written = true);

        // Assert
        Assert.True(written);
    }

    [Fact]
    public void ReadProperty_StateProperty_GuestCanRead()
    {
        // Arrange - State.Read defaults to Guest
        SetupUserWithRoles(DefaultRoles.Guest, DefaultRoles.Anonymous);
        var subject = CreateTestSubject();
        var property = new PropertyReference(subject, "Temperature"); // No [Configuration], defaults to State
        var context = new PropertyReadContext(property);
        var value = 25.5;

        // Act
        var result = _interceptor.ReadProperty(ref context, (ref PropertyReadContext _) => value);

        // Assert - Guest can read State
        Assert.Equal(value, result);
    }

    [Fact]
    public void WriteProperty_StateProperty_OperatorCanWrite()
    {
        // Arrange - State.Write defaults to Operator
        SetupUserWithRoles(DefaultRoles.Operator, DefaultRoles.User);
        var subject = CreateTestSubject();
        var property = new PropertyReference(subject, "Temperature");
        var writeContext = new PropertyWriteContext<double>(property, 20.0, 25.0);
        var written = false;

        // Act
        _interceptor.WriteProperty(ref writeContext, (ref PropertyWriteContext<double> _) => written = true);

        // Assert
        Assert.True(written);
    }

    [Fact]
    public void ReadProperty_UnauthorizedForConfiguration_ReturnsDefault()
    {
        // Arrange - Anonymous user cannot read Configuration
        SetupUserWithRoles(DefaultRoles.Anonymous);
        var subject = CreateTestSubject();
        var property = new PropertyReference(subject, "ApiKey");
        var context = new PropertyReadContext(property);

        // Act
        var result = _interceptor.ReadProperty(ref context, (ref PropertyReadContext _) => "should-not-see");

        // Assert - Returns default (null for reference types) instead of throwing
        Assert.Null(result);
    }

    #endregion
}
