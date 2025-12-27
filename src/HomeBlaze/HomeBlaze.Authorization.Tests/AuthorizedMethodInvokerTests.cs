using System.Collections.Concurrent;
using System.Security.Claims;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Services;
using HomeBlaze.Authorization.Configuration;
using HomeBlaze.Authorization.Context;
using HomeBlaze.Authorization.Roles;
using HomeBlaze.Authorization.Services;
using Moq;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// Tests for AuthorizedMethodInvoker method access control.
/// </summary>
public class AuthorizedMethodInvokerTests : IDisposable
{
    private readonly Mock<ISubjectMethodInvoker> _innerInvokerMock;
    private readonly IAuthorizationResolver _resolver;
    private readonly AuthorizedMethodInvoker _invoker;

    public AuthorizedMethodInvokerTests()
    {
        _innerInvokerMock = new Mock<ISubjectMethodInvoker>();
        var options = new AuthorizationOptions();
        _resolver = new AuthorizationResolver(options);
        _invoker = new AuthorizedMethodInvoker(_innerInvokerMock.Object, _resolver);

        // Clear context before each test
        AuthorizationContext.Clear();
    }

    public void Dispose()
    {
        AuthorizationContext.Clear();
    }

    [Fact]
    public async Task InvokeAsync_WithAuthorizedUser_CallsInnerInvoker()
    {
        // Arrange
        SetupUserWithRoles(DefaultRoles.Operator);
        var subject = CreateMockSubject();
        var method = CreateOperationMethod("TurnOn");
        var expectedResult = MethodInvocationResult.Succeeded("OK");
        _innerInvokerMock
            .Setup(i => i.InvokeAsync(subject, method, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _invoker.InvokeAsync(subject, method, [], CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        _innerInvokerMock.Verify(i => i.InvokeAsync(subject, method, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_WithUnauthorizedUser_ReturnsFailedResult()
    {
        // Arrange - Guest cannot invoke Operation (requires Operator)
        SetupUserWithRoles(DefaultRoles.Guest);
        var subject = CreateMockSubject();
        var method = CreateOperationMethod("TurnOn");

        // Act
        var result = await _invoker.InvokeAsync(subject, method, [], CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.IsType<UnauthorizedAccessException>(result.Exception);
        Assert.Contains("Access denied", result.Exception!.Message);
        Assert.Contains("TurnOn", result.Exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_QueryMethod_RequiresUserRole()
    {
        // Arrange - Guest cannot invoke Query (requires User)
        SetupUserWithRoles(DefaultRoles.Guest);
        var subject = CreateMockSubject();
        var method = CreateQueryMethod("GetStatus");

        // Act
        var result = await _invoker.InvokeAsync(subject, method, [], CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.IsType<UnauthorizedAccessException>(result.Exception);
    }

    [Fact]
    public async Task InvokeAsync_QueryMethod_WithUserRole_Succeeds()
    {
        // Arrange
        SetupUserWithRoles(DefaultRoles.User);
        var subject = CreateMockSubject();
        var method = CreateQueryMethod("GetStatus");
        _innerInvokerMock
            .Setup(i => i.InvokeAsync(subject, method, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MethodInvocationResult.Succeeded("Status OK"));

        // Act
        var result = await _invoker.InvokeAsync(subject, method, [], CancellationToken.None);

        // Assert
        Assert.True(result.Success);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotCallInnerInvoker_WhenUnauthorized()
    {
        // Arrange
        SetupUserWithRoles(DefaultRoles.Anonymous);
        var subject = CreateMockSubject();
        var method = CreateOperationMethod("TurnOn");

        // Act
        await _invoker.InvokeAsync(subject, method, [], CancellationToken.None);

        // Assert
        _innerInvokerMock.Verify(
            i => i.InvokeAsync(It.IsAny<IInterceptorSubject>(), It.IsAny<SubjectMethodInfo>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void SetupUserWithRoles(params string[] roles)
    {
        var identity = new ClaimsIdentity("TestAuth");
        var user = new ClaimsPrincipal(identity);
        AuthorizationContext.SetUser(user, roles);
    }

    private static IInterceptorSubject CreateMockSubject()
    {
        var contextMock = new Mock<IInterceptorSubjectContext>();
        var subjectMock = new Mock<IInterceptorSubject>();
        subjectMock.Setup(s => s.Context).Returns(contextMock.Object);
        subjectMock.Setup(s => s.Data).Returns(new ConcurrentDictionary<(string?, string), object?>());
        return subjectMock.Object;
    }

    private static SubjectMethodInfo CreateOperationMethod(string name)
    {
        var methodInfo = typeof(TestSubject).GetMethod(name)!;
        var attribute = new OperationAttribute { Title = name };
        return new SubjectMethodInfo(methodInfo, attribute, SubjectMethodKind.Operation, [], null);
    }

    private static SubjectMethodInfo CreateQueryMethod(string name)
    {
        var methodInfo = typeof(TestSubject).GetMethod(name)!;
        var attribute = new QueryAttribute { Title = name };
        return new SubjectMethodInfo(methodInfo, attribute, SubjectMethodKind.Query, [], typeof(string));
    }

    // Test subject with methods
    private class TestSubject
    {
        public void TurnOn() { }
        public void TurnOff() { }
        public string GetStatus() => "OK";
    }
}
