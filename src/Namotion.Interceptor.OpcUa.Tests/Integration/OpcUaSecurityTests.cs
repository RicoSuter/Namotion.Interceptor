using System.Security.Cryptography.X509Certificates;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Covers application certificates, trust and endpoint selection between a real client and server. The client and the
/// server use separate certificate stores, so each test can tell which side wrote, rejected or trusted a certificate.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaSecurityTests
{
    private const string ServerApplicationUri = "urn:test:Namotion.Interceptor.Server";
    private const string OtherServerApplicationUri = "urn:test:Namotion.Interceptor.Server.Renamed";

    // Covers up to 15 s of MaxReconnectDuration before a manual reconnect, the 5 s health check, the 1 s retry and the
    // secured handshake, with the margin of the existing reconnection tests, which use 90 s.
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(90);

    private readonly ITestOutputHelper _output;

    public OpcUaSecurityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheClientRequiresSigning_ThenItCreatesItsOwnCertificateAndConnectsSecurely()
    {
        // Arrange
        await using var test = await SecurityTest.CreateAsync(_output);
        await test.StartServerAsync();

        // Act
        var client = await test.StartClientAsync(config =>
        {
            config.MinimumSecurityMode = MessageSecurityMode.Sign;
            config.AutoAcceptUntrustedCertificates = true;
        });
        await WaitUntilConnectedAsync(client);

        // Assert
        var session = client.Source!.CurrentSession!;
        Assert.True(session.Endpoint.SecurityMode >= MessageSecurityMode.Sign);

        using var certificate = LoadOwnCertificate(test.ClientStore);
        Assert.Equal("CN=Namotion.Interceptor.Client, O=Namotion", certificate.Subject);
        Assert.True(X509Utils.IsSelfSigned(certificate));
        Assert.True(X509Utils.CompareApplicationUriWithCertificate(
            certificate, $"urn:{System.Net.Dns.GetHostName()}:Namotion.Interceptor:Namotion.Interceptor.Client"));
        Assert.Single(GetFiles(Path.Combine(test.ClientStore, "own", "private")));
    }

    [Fact]
    public async Task WhenTheServerCertificateIsNotTrusted_ThenTheClientConnectsOnceItIsMovedToTrusted()
    {
        // Arrange
        await using var test = await SecurityTest.CreateAsync(_output);
        await test.StartServerAsync();
        using var serverCertificate = LoadOwnCertificate(test.ServerStore);
        var client = await test.StartClientAsync(config => config.MinimumSecurityMode = MessageSecurityMode.Sign);
        await WaitForRejectedCertificateAsync(test.ClientStore, serverCertificate,
            "The client should write the untrusted server certificate to its rejected store");
        await WaitForStatusCodeAsync(client, test.Logger, StatusCodes.BadCertificateUntrusted);

        var error = Assert.IsType<InvalidOperationException>(client.Source!.Diagnostics.LastError);
        Assert.Contains(serverCertificate.Thumbprint, error.Message);
        Assert.Contains(nameof(OpcUaClientConfiguration.AutoAcceptUntrustedCertificates), error.Message);
        Assert.Null(client.Source.CurrentSession);

        // Act
        await TrustRejectedCertificatesAsync(test.ClientStore);

        // Assert
        await WaitUntilConnectedAsync(client);
        Assert.True(client.Source.CurrentSession!.Endpoint.SecurityMode >= MessageSecurityMode.Sign);
    }

    [Fact]
    public async Task WhenTheServerDoesNotTrustTheClientCertificate_ThenTheClientConnectsOnceTheServerTrustsIt()
    {
        // Arrange
        await using var test = await SecurityTest.CreateAsync(_output);
        await test.StartServerAsync(autoAcceptUntrustedCertificates: false);
        var client = await test.StartClientAsync(config =>
        {
            config.MinimumSecurityMode = MessageSecurityMode.Sign;
            config.AutoAcceptUntrustedCertificates = true;
        });
        await AsyncTestHelpers.WaitUntilAsync(
            () => GetFiles(Path.Combine(test.ClientStore, "own", "certs")).Length > 0,
            message: "The client should create its certificate for the secured endpoint");
        using var clientCertificate = LoadOwnCertificate(test.ClientStore);
        await WaitForRejectedCertificateAsync(test.ServerStore, clientCertificate,
            "The server should write the untrusted client certificate to its rejected store");
        await WaitForStatusCodeAsync(client, test.Logger, StatusCodes.BadSecurityChecksFailed);

        var error = Assert.IsType<InvalidOperationException>(client.Source!.Diagnostics.LastError);
        Assert.Contains(clientCertificate.Thumbprint, error.Message);
        Assert.DoesNotContain(nameof(OpcUaClientConfiguration.AutoAcceptUntrustedCertificates), error.Message);
        Assert.Null(client.Source.CurrentSession);

        // Act
        await TrustRejectedCertificatesAsync(test.ServerStore);

        // Assert
        await WaitUntilConnectedAsync(client);
        Assert.True(client.Source.CurrentSession!.Endpoint.SecurityMode >= MessageSecurityMode.Sign);
    }

    [Fact]
    public async Task WhenAUsernameLoginRunsOverAnUnsecuredChannel_ThenTheServerCertificateMustBeTrusted()
    {
        // Arrange
        await using var test = await SecurityTest.CreateAsync(_output);
        await test.StartServerAsync();
        using var serverCertificate = LoadOwnCertificate(test.ServerStore);
        var client = await test.StartClientAsync(config =>
            config.CreateUserIdentity = _ => Task.FromResult(new UserIdentity("user", "password"u8.ToArray())));
        await WaitForRejectedCertificateAsync(test.ClientStore, serverCertificate,
            "The client should check the server certificate before it encrypts the username token");
        await WaitForStatusCodeAsync(client, test.Logger, StatusCodes.BadCertificateUntrusted);
        Assert.Null(client.Source!.CurrentSession);

        // Act
        await TrustRejectedCertificatesAsync(test.ClientStore);

        // Assert
        await WaitUntilConnectedAsync(client);
        Assert.Equal(MessageSecurityMode.None, client.Source.CurrentSession!.Endpoint.SecurityMode);
        Assert.Empty(GetFiles(Path.Combine(test.ClientStore, "own")));
    }

    [Fact]
    public async Task WhenSigningIsRequiredAndTheServerOffersOnlyUnsecuredEndpoints_ThenConnectingFails()
    {
        // Arrange
        await using var test = await SecurityTest.CreateAsync(_output);
        await test.StartServerAsync(configureApplication: application => application.ServerConfiguration.SecurityPolicies =
        [
            new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None }
        ]);

        // Act
        var client = await test.StartClientAsync(config =>
        {
            config.MinimumSecurityMode = MessageSecurityMode.Sign;
            config.AutoAcceptUntrustedCertificates = true;
        });

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Source!.Diagnostics.LastError is InvalidOperationException,
            message: "The client should refuse to fall back to an unsecured endpoint");
        var error = Assert.IsType<InvalidOperationException>(client.Source!.Diagnostics.LastError);
        Assert.Contains(nameof(OpcUaClientConfiguration.MinimumSecurityMode), error.Message);
        Assert.Null(client.Source.CurrentSession);
        Assert.Empty(GetFiles(Path.Combine(test.ClientStore, "own")));
    }

    [Fact]
    public async Task WhenTheServerOffersOnlySecuredEndpoints_ThenAClientWithoutAMinimumConnectsSecurely()
    {
        // Arrange
        await using var test = await SecurityTest.CreateAsync(_output);
        await test.StartServerAsync(configureApplication: application => application.ServerConfiguration.SecurityPolicies =
        [
            new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.Sign, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 }
        ]);

        // Act
        var client = await test.StartClientAsync(config =>
        {
            config.MinimumSecurityMode = MessageSecurityMode.None;
            config.AutoAcceptUntrustedCertificates = true;
        });
        await WaitUntilConnectedAsync(client);

        // Assert
        var endpoint = client.Source!.CurrentSession!.Endpoint;
        Assert.Equal(MessageSecurityMode.Sign, endpoint.SecurityMode);
        Assert.Equal(SecurityPolicies.Basic256Sha256, endpoint.SecurityPolicyUri);
    }

    [Fact]
    public async Task WhenSignAndEncryptIsRequired_ThenAnEncryptedEndpointIsSelected()
    {
        // Arrange
        await using var test = await SecurityTest.CreateAsync(_output);
        await test.StartServerAsync();

        // Act
        var client = await test.StartClientAsync(config =>
        {
            config.MinimumSecurityMode = MessageSecurityMode.SignAndEncrypt;
            config.AutoAcceptUntrustedCertificates = true;
        });
        await WaitUntilConnectedAsync(client);

        // Assert
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, client.Source!.CurrentSession!.Endpoint.SecurityMode);
    }

    [Fact]
    public async Task WhenTheServerRestarts_ThenItKeepsItsCertificateAndAStrictClientReconnectsWithoutTrustingItAgain()
    {
        // Arrange
        await using var test = await SecurityTest.CreateAsync(_output);
        var server = await test.StartServerAsync();
        using var serverCertificate = LoadOwnCertificate(test.ServerStore);
        ProvisionTrustedCertificate(test.ServerStore, test.ClientStore);
        var client = await test.StartClientAsync(config =>
        {
            config.MinimumSecurityMode = MessageSecurityMode.Sign;
            config.KeepAliveInterval = TimeSpan.FromSeconds(2);
            config.ReconnectInterval = TimeSpan.FromSeconds(2);
        });
        await WaitUntilConnectedAsync(client);

        // Act
        await server.RestartAsync();
        server.Root!.Name = "AfterRestart";

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "AfterRestart",
            () => $"The client should reconnect to the restarted server without trusting it again. Last error: {client.Source?.Diagnostics.LastError}",
            timeout: ReconnectTimeout);

        using var restartedServerCertificate = LoadOwnCertificate(test.ServerStore);
        Assert.Equal(serverCertificate.Thumbprint, restartedServerCertificate.Thumbprint);
        Assert.Empty(GetFiles(Path.Combine(test.ClientStore, "rejected")));
    }

    [Fact]
    public async Task WhenTheServerHasAnApplicationUri_ThenItsCertificateCarriesIt()
    {
        // Arrange
        await using var test = await SecurityTest.CreateAsync(_output);

        // Act
        await test.StartServerAsync(applicationUri: ServerApplicationUri);
        var client = await test.StartClientAsync(config =>
        {
            config.MinimumSecurityMode = MessageSecurityMode.Sign;
            config.AutoAcceptUntrustedCertificates = true;
        });
        await WaitUntilConnectedAsync(client);

        // Assert
        using var serverCertificate = LoadOwnCertificate(test.ServerStore);
        var endpoint = client.Source!.CurrentSession!.Endpoint;
        Assert.Equal(ServerApplicationUri, endpoint.Server.ApplicationUri);
        Assert.True(X509Utils.CompareApplicationUriWithCertificate(serverCertificate, ServerApplicationUri));
        using var endpointCertificate = ParseServerCertificate(endpoint);
        Assert.Equal(serverCertificate.Thumbprint, endpointCertificate.Thumbprint);
    }

    [Fact]
    public async Task WhenTheServerApplicationUriChanges_ThenTheServerReplacesItsCertificateAndAStrictClientMustTrustTheNewOne()
    {
        // Arrange
        await using var test = await SecurityTest.CreateAsync(_output);
        Action<OpcUaClientConfiguration> configureClient = config => config.MinimumSecurityMode = MessageSecurityMode.Sign;
        await test.StartServerAsync(applicationUri: ServerApplicationUri);
        using var originalCertificate = LoadOwnCertificate(test.ServerStore);
        ProvisionTrustedCertificate(test.ServerStore, test.ClientStore);
        var trustingClient = await test.StartClientAsync(configureClient);
        await WaitUntilConnectedAsync(trustingClient);
        await test.StopClientAsync(trustingClient);

        // Act
        await test.StartServerAsync(applicationUri: OtherServerApplicationUri);
        var client = await test.StartClientAsync(configureClient);

        // Assert
        using var replacementCertificate = LoadOwnCertificate(test.ServerStore);
        Assert.NotEqual(originalCertificate.Thumbprint, replacementCertificate.Thumbprint);
        Assert.True(X509Utils.CompareApplicationUriWithCertificate(replacementCertificate, OtherServerApplicationUri));

        await WaitForRejectedCertificateAsync(test.ClientStore, replacementCertificate,
            "The client should reject the replacement certificate it does not trust yet");
        await WaitForStatusCodeAsync(client, test.Logger, StatusCodes.BadCertificateUntrusted);

        // Act
        await TrustRejectedCertificatesAsync(test.ClientStore);

        // Assert
        await WaitUntilConnectedAsync(client);
        Assert.Equal(OtherServerApplicationUri, client.Source!.CurrentSession!.Endpoint.Server.ApplicationUri);
    }

    [Fact]
    public async Task WhenTheEndpointIsUnsecured_ThenTheClientStoreHoldsNoCertificate()
    {
        // Arrange
        await using var test = await SecurityTest.CreateAsync(_output);
        await test.StartServerAsync();

        // Act
        var client = await test.StartClientAsync();
        await WaitUntilConnectedAsync(client);

        // Assert
        Assert.Equal(MessageSecurityMode.None, client.Source!.CurrentSession!.Endpoint.SecurityMode);
        Assert.Empty(GetFiles(test.ClientStore));
    }

    private static Task WaitUntilConnectedAsync(OpcUaTestClient<TestRoot> client) =>
        AsyncTestHelpers.WaitUntilAsync(
            () => client.Source!.CurrentSession is not null &&
                  client.Source.Diagnostics.MonitoredItemCount > 0 &&
                  client.Root!.Connected,
            () => $"The client should connect and load the initial state. Last error: {client.Source?.Diagnostics.LastError}");

    private static async Task WaitForStatusCodeAsync(OpcUaTestClient<TestRoot> client, TestLogger logger, StatusCode expected)
    {
        await AsyncTestHelpers.WaitUntilAsync(
            () => GetLastStatusCode(client) == expected,
            () => $"The client should report {expected}. Last error: {client.Source?.Diagnostics.LastError}");
        logger.Log($"Observed status code {GetLastStatusCode(client)} in: {client.Source!.Diagnostics.LastError!.Message}");
    }

    private static Task WaitForRejectedCertificateAsync(string storePath, X509Certificate2 certificate, string message) =>
        AsyncTestHelpers.WaitUntilAsync(
            () => GetFiles(Path.Combine(storePath, "rejected", "certs"))
                .Any(file => Path.GetFileName(file).Contains(certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)),
            message: message);

    private static StatusCode? GetLastStatusCode(OpcUaTestClient<TestRoot> client) =>
        (client.Source?.Diagnostics.LastError?.InnerException as ServiceResultException)?.StatusCode;

    private static X509Certificate2 ParseServerCertificate(EndpointDescription endpoint) =>
        X509CertificateLoader.LoadCertificate(endpoint.ServerCertificate);

    // A server's certificate exists once StartServerAsync returns, because the connector start runs inline up to the bind.
    /// <summary>
    /// Loads the one application certificate in the own store, which holds a single certificate in these tests.
    /// </summary>
    private static X509Certificate2 LoadOwnCertificate(string storePath) =>
        X509CertificateLoader.LoadCertificateFromFile(Assert.Single(GetFiles(Path.Combine(storePath, "own", "certs"))));

    private static string[] GetFiles(string path) =>
        Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories) : [];

    /// <summary>
    /// Moves every rejected certificate to the trusted folder. A move that fails because the SDK is rewriting the file
    /// after another rejection, which happens on Windows, is retried.
    /// </summary>
    private static Task TrustRejectedCertificatesAsync(string storePath)
    {
        var trusted = Path.Combine(storePath, "trusted", "certs");
        Directory.CreateDirectory(trusted);
        return AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                try
                {
                    foreach (var file in GetFiles(Path.Combine(storePath, "rejected", "certs")))
                    {
                        File.Move(file, Path.Combine(trusted, Path.GetFileName(file)), overwrite: true);
                    }

                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            },
            message: "The rejected certificates should be moved to the trusted folder");
    }

    /// <summary>
    /// Copies the own certificate of one store to the trusted folder of another, as an operator provisions trust.
    /// </summary>
    private static void ProvisionTrustedCertificate(string fromStorePath, string toStorePath)
    {
        var certificate = Assert.Single(GetFiles(Path.Combine(fromStorePath, "own", "certs")));
        var trusted = Path.Combine(toStorePath, "trusted", "certs");
        Directory.CreateDirectory(trusted);
        File.Copy(certificate, Path.Combine(trusted, Path.GetFileName(certificate)));
    }

    /// <summary>
    /// Owns the port, the server, the clients and the certificate stores of one test, and releases them in that order.
    /// </summary>
    private sealed class SecurityTest : IAsyncDisposable
    {
        private readonly PortLease _port;
        private readonly string _storeRoot;
        private readonly List<OpcUaTestClient<TestRoot>> _clients = [];
        private OpcUaTestServer<TestRoot>? _server;

        private SecurityTest(TestLogger logger, PortLease port, string storeRoot)
        {
            Logger = logger;
            _port = port;
            _storeRoot = storeRoot;
        }

        public TestLogger Logger { get; }

        public string ServerStore => Path.Combine(_storeRoot, "server");

        public string ClientStore => Path.Combine(_storeRoot, "client");

        public static async Task<SecurityTest> CreateAsync(ITestOutputHelper output)
        {
            var port = await OpcUaTestPortPool.AcquireAsync();
            var storeRoot = Path.Combine(AppContext.BaseDirectory, "pki-security", Guid.NewGuid().ToString("N"));
            return new SecurityTest(new TestLogger(output), port, storeRoot);
        }

        /// <summary>
        /// Starts a server on the test's port and store, after stopping the one started before, if any.
        /// </summary>
        public async Task<OpcUaTestServer<TestRoot>> StartServerAsync(
            Action<Opc.Ua.ApplicationConfiguration>? configureApplication = null,
            string? applicationUri = null,
            bool autoAcceptUntrustedCertificates = true)
        {
            if (_server is not null)
            {
                await _server.DisposeAsync();
            }

            _server = new OpcUaTestServer<TestRoot>(Logger);
            await _server.StartAsync(
                context => new TestRoot(context),
                (_, root) =>
                {
                    root.Connected = true;
                    root.Name = "Initial";
                },
                baseAddress: _port.BaseAddress,
                certificateStoreBasePath: ServerStore,
                configureApplication: configureApplication,
                applicationUri: applicationUri,
                autoAcceptUntrustedCertificates: autoAcceptUntrustedCertificates);

            return _server;
        }

        /// <summary>
        /// Starts a client on the test's client store without waiting for it to connect. It retries a failed
        /// connect after a second, so a test that fixes the trust does not wait for the default retry time.
        /// </summary>
        public async Task<OpcUaTestClient<TestRoot>> StartClientAsync(Action<OpcUaClientConfiguration>? configure = null)
        {
            var client = new OpcUaTestClient<TestRoot>(Logger, config =>
            {
                config.RetryTime = TimeSpan.FromSeconds(1);
                configure?.Invoke(config);
            });
            _clients.Add(client);

            await client.StartAsync(
                context => new TestRoot(context),
                root => root.Connected,
                serverUrl: _port.ServerUrl,
                certificateStoreBasePath: ClientStore,
                waitUntilConnected: false);

            return client;
        }

        public async Task StopClientAsync(OpcUaTestClient<TestRoot> client)
        {
            _clients.Remove(client);
            await client.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var client in _clients)
                {
                    await client.DisposeAsync();
                }

                if (_server is not null)
                {
                    await _server.DisposeAsync();
                }
            }
            finally
            {
                _port.Dispose();
                try
                {
                    if (Directory.Exists(_storeRoot))
                    {
                        Directory.Delete(_storeRoot, recursive: true);
                    }
                }
                catch (IOException exception)
                {
                    Logger.Log($"Failed to delete the certificate stores at '{_storeRoot}': {exception.Message}");
                }
            }
        }
    }
}
