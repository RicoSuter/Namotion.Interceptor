# OPC UA client certificates and server trust

Design for issue #968. The OPC UA client cannot connect with `UseSecurity = true`, and once it can, it must not trust every server.

## Problem

1. **The client never loads its own application certificate.** With security off none is needed. With security on, the SDK looks up the client certificate with its private key through `SecurityConfiguration.FindApplicationCertificateAsync`, finds none, and every connect attempt fails with "ApplicationCertificate for the security profile ... cannot be found". A certificate file in `{CertificateStoreBasePath}/own` does not help: the SDK's directory store enumerates certificates without private keys, so only a certificate the application loaded itself is found. The server loads its certificate through `ApplicationInstance.CheckApplicationInstanceCertificatesAsync`; the client never calls an equivalent.
2. **The client accepts every server certificate.** `OpcUaClientConfiguration.CreateApplicationInstanceAsync` hardcodes `AutoAcceptUntrustedCertificates = true` with no option, while the server exposes the same setting with a default of false. A secured client connection would be encrypted without authenticating the server.
3. **The docs say nothing about certificates.** They recommend `UseSecurity = true` for production without describing where the client certificate lives or how either side comes to trust the other.

Username identities from `CreateUserIdentity` do not need a client certificate; they encrypt the token with the server's certificate.

## Decisions

### Server trust: strict by default

- `OpcUaClientConfiguration.AutoAcceptUntrustedCertificates`, default `false`, replaces the hardcoded `true` and is passed to `SecurityConfiguration.AutoAcceptUntrustedCertificates`. Name and meaning mirror `OpcUaServerConfiguration.AutoAcceptUntrustedCertificates`.
- It governs every server certificate check the SDK makes: on secured channels, and before encrypting a username token over an unsecured channel (`Session.cs`, `requireEncryption` for the identity policy). Anonymous clients on unsecured channels are not checked by the SDK and are unaffected.
- A rejected server certificate is written to `{CertificateStoreBasePath}/rejected` by the SDK. Moving it to `{CertificateStoreBasePath}/trusted`, or provisioning that store up front, makes the client trust the server.

### Client certificate: created on first use, configurable

- `OpcUaClientConfiguration.AutoCreateApplicationCertificate`, default `true`, only matters when `UseSecurity` is on.
  - On: the client calls `ApplicationInstance.CheckApplicationInstanceCertificatesAsync(silent: true)`, which loads the certificate from `{CertificateStoreBasePath}/own` or creates a self-signed one there.
  - Off: the client calls `ApplicationCertificate.LoadPrivateKeyExAsync` and fails when no certificate is found, with a message naming the store path and this option.
- Default on because creating a certificate grants nothing: the server still has to trust it, and the server already creates its own certificate the same way. Off suits managed PKI, where a missing certificate must fail loudly, and containers without a persistent certificate store, where each restart would create a new certificate the server has to trust again.
- With `UseSecurity` off, no certificate is created, loaded or written.

### Customization

- `CreateApplicationInstanceAsync` stays virtual for full control over the SDK configuration.
- New virtual method `OpcUaClientConfiguration.LoadApplicationCertificateAsync(ApplicationInstance application, CancellationToken cancellationToken)`. Contract: on return, the application certificate is loaded with its private key. The default implementation does what `AutoCreateApplicationCertificate` selects. Override it to load with a password provider, from another store type or from a hardware security module.
- The default implementation does nothing when the configured application certificate is already loaded with its private key, so an override of `CreateApplicationInstanceAsync` that sets it is not replaced.

### Where the step runs

- In `SessionManager.CreateSessionAsync`, before endpoint discovery, when `UseSecurity` is on. That method receives the application instance and the configuration for the initial connect and for every manual reconnect.
- Not inside `CreateApplicationInstanceAsync`, so overriding that method does not silently drop the certificate step.
- SDK reconnects reuse the session's already loaded certificate and do not run the step.
- A failure goes through the existing connect failure path: logged, reported in `Diagnostics.LastError`, retried after `ReconnectInterval`.

## Breaking change

Clients with security off that log in with a username now validate the server certificate before sending the token. Migration: add the server certificate to `{CertificateStoreBasePath}/trusted`, or set `AutoAcceptUntrustedCertificates = true` to keep the previous behavior. Clients with `UseSecurity = true` could not connect before, so nothing relying on them changes.

## HomeBlaze

The HomeBlaze OPC UA client builds `CreateUserIdentity` from a configured username and password over an unsecured channel, so it is affected. It gets a new `[Configuration]` property `AcceptUntrustedServerCertificates`, default `false`, passed to `AutoAcceptUntrustedCertificates`. Users with a login either trust the server certificate in the client's certificate store or enable the setting.

## Tests

- Unit, against a temporary certificate store:
  - `AutoAcceptUntrustedCertificates` reaches the SDK security configuration.
  - With auto-create on, the first call creates a certificate and loads it with its private key; a second call reuses it.
  - With auto-create off and no certificate, the step fails with the message naming the store path and the option.
  - A certificate already loaded with its private key is not replaced.
  - With `UseSecurity` off, nothing is written to the store.
- Integration:
  - A secure connection works with the library's own certificate step. The test client's certificate-loading subclass is removed.
  - With strict trust, an untrusted server is rejected: the failure shows in `Diagnostics.LastError` and the server certificate lands in `rejected`; after it is moved to `trusted`, the client connects.
  - The subscription transfer test sets `AutoAcceptUntrustedCertificates = true`, since it tests transfers, not trust.
- Not covered by a test: username logins over unsecured channels, because the test server does not validate usernames. Covered by reading the SDK's `Session` code.

## Docs

- `docs/connectors-opcua-client.md`, Transport Security: where the client certificate lives and when it is created, the trust workflow on both sides through the `rejected` and `trusted` stores, both options, both virtual methods, and the migration note.
- The client configuration table: the two new options.
