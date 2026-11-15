using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Opc.Ua;
using Opc.Ua.Identity;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

public class OpcUaServerConfigurationTests
{
    [Fact]
    public void WhenTheServerConfigurationIsCreated_ThenItKeepsItsCertificateStore()
    {
        // Act
        var configuration = new OpcUaServerConfiguration();

        // Assert
        Assert.False(configuration.CleanCertificateStore);
    }

    [Fact]
    public async Task WhenNoAuthenticatorIsConfigured_ThenOnlyAnonymousLoginsAreAdvertised()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration();

        // Act
        var application = await configuration.CreateApplicationInstanceAsync();

        // Assert
        var policy = Assert.Single(application.ApplicationConfiguration!.ServerConfiguration!.UserTokenPolicies.ToArray()!);
        Assert.Equal(UserTokenType.Anonymous, policy.TokenType);
    }

    [Fact]
    public async Task WhenAuthenticatorsAreConfigured_ThenTheirTokenTypesAreAdvertisedInsteadOfAnonymous()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            UserTokenAuthenticators =
            [
                new TestAuthenticator(UserTokenType.UserName),
                new TestAuthenticator(UserTokenType.Certificate)
            ]
        };

        // Act
        var application = await configuration.CreateApplicationInstanceAsync();

        // Assert
        var policies = application.ApplicationConfiguration!.ServerConfiguration!.UserTokenPolicies.ToArray()!;
        Assert.Equal([UserTokenType.UserName, UserTokenType.Certificate], policies.Select(policy => policy.TokenType));
        Assert.All(policies, policy => Assert.Equal(SecurityPolicies.Basic256Sha256, policy.SecurityPolicyUri));
    }

    [Fact]
    public async Task WhenAnApplicationUriIsSet_ThenTheApplicationUsesIt()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration { ApplicationUri = "urn:test:server" };

        // Act
        var application = await configuration.CreateApplicationInstanceAsync();

        // Assert
        Assert.Equal("urn:test:server", application.ApplicationConfiguration!.ApplicationUri);
    }

    [Fact]
    public async Task WhenNoApplicationUriIsSet_ThenItIsDerivedFromTheHostAndApplicationName()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration();

        // Act
        var application = await configuration.CreateApplicationInstanceAsync();

        // Assert
        Assert.Null(configuration.ApplicationUri);
        Assert.Equal(
            $"urn:{System.Net.Dns.GetHostName()}:Namotion.Interceptor:{configuration.ApplicationName}",
            application.ApplicationConfiguration!.ApplicationUri);
    }

    [Fact]
    public void WhenTwoAuthenticatorsHandleTheSameTokenType_ThenValidationFails()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            UserTokenAuthenticators =
            [
                new TestAuthenticator(UserTokenType.UserName),
                new TestAuthenticator(UserTokenType.UserName)
            ]
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Equal(nameof(OpcUaServerConfiguration.UserTokenAuthenticators), exception.ParamName);
    }

    [Fact]
    public void WhenTwoIssuedTokenAuthenticatorsHaveDifferentIssuers_ThenValidationPasses()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            UserTokenAuthenticators =
            [
                new TestIssuerAuthenticator("urn:issuer:a"),
                new TestIssuerAuthenticator("urn:issuer:b")
            ]
        };

        // Act
        var exception = Record.Exception(configuration.Validate);

        // Assert
        Assert.Null(exception);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void WhenTheApplicationUriIsEmpty_ThenValidationFails(string applicationUri)
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration { ApplicationUri = applicationUri };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Equal(nameof(OpcUaServerConfiguration.ApplicationUri), exception.ParamName);
    }

    [Fact]
    public void WhenTheServerIsCreatedWithAnInvalidConfiguration_ThenItThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var subject = new DeliveryRuleTestRoot(context);
        var configuration = new OpcUaServerConfiguration { ApplicationUri = " " };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => new OpcUaSubjectServer(subject, configuration, NullLogger.Instance));
        Assert.Equal(nameof(OpcUaServerConfiguration.ApplicationUri), exception.ParamName);
    }

    private sealed class TestIssuerAuthenticator(string issuerUri) : IIssuerTokenAuthenticator
    {
        public UserTokenType TokenType => UserTokenType.IssuedToken;

        public string? IssuedTokenProfileUri => "http://opcfoundation.org/UA/UserToken#JWT";

        public string? IssuerUri => issuerUri;

        public ValueTask<AuthenticationResult> AuthenticateAsync(AuthenticationContext context, CancellationToken ct = default) =>
            new(AuthenticationResult.NotHandled);
    }

    private sealed class TestAuthenticator(UserTokenType tokenType) : IUserTokenAuthenticator
    {
        public UserTokenType TokenType => tokenType;

        public string? IssuedTokenProfileUri => null;

        public ValueTask<AuthenticationResult> AuthenticateAsync(AuthenticationContext context, CancellationToken ct = default) =>
            new(AuthenticationResult.NotHandled);
    }
}
