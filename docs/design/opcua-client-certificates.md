# OPC UA client certificates and server trust

Design for issue #968. The OPC UA client cannot connect with security, and once it can, it must not trust every server. All references to the OPC Foundation SDK are to version 1.5.378.145.

## Problem

1. **The client never loads its own application certificate.** The SDK looks up the client certificate with its private key through `SecurityConfiguration.FindApplicationCertificateAsync`, finds none, and every connect to a secured endpoint fails with "ApplicationCertificate for the security profile ... cannot be found" (`DefaultSessionFactory.cs:255`, `Session.cs:4566`). A certificate file in the store does not help: the directory store enumerates certificates without private keys (`DirectoryCertificateStore.cs:189-198`), so only a certificate the application loaded itself is found. The server loads its certificate through `ApplicationInstance.CheckApplicationInstanceCertificatesAsync`; the client never calls an equivalent.
2. **The client accepts every server certificate.** `CreateApplicationInstanceAsync` hardcodes `AutoAcceptUntrustedCertificates = true`, while the server exposes the setting with a default of false. A secured connection would be encrypted without authenticating the server.
3. **`UseSecurity = true` silently downgrades.** Endpoint selection falls back to the first endpoint with a matching scheme, whatever its security (`CoreClientUtils.cs:338-342`), so against a server without secured endpoints the client connects without security.
4. **The server regenerates its certificate on every start.** `CleanCertificateStore` defaults to true and deletes the server's own store at startup, so a client that trusts the server loses that trust on every restart. When client and server share a store path, it also deletes the client's private key.
5. **The docs say nothing about certificates,** and their example for tightening trust in an override has no effect: the validator copies `AutoAcceptUntrustedCertificates` only during `CertificateValidator.UpdateAsync` (`CertificateValidator.cs:221-224`), which the base method has already called.

## Decisions

### Security requirement: `RequireSecurity`

- `UseSecurity` is renamed to `RequireSecurity`, default `false`.
- `true`: the client connects only to a signed or encrypted endpoint and fails with a clear message when the server offers none. No downgrade.
- `false`: an unsecured endpoint is preferred, and a secured one is used when the server offers nothing else.

### Server trust: strict by default

- `OpcUaClientConfiguration.AutoAcceptUntrustedCertificates`, default `false`, replaces the hardcoded `true`. Name and meaning mirror `OpcUaServerConfiguration.AutoAcceptUntrustedCertificates`.
- The SDK checks the server certificate when it opens a secured channel and on every renewal of it (`UaSCBinaryChannel.Asymmetric.cs:1013-1025`), when it opens a session whose channel or identity policy requires encryption (`Session.cs:1097-1131`, `3817-3824`), and before encrypting a non-anonymous identity token (`Session.cs:1457-1467`). Affected: every secured connection, username and certificate identities over unsecured channels, and anonymous clients whose server names a security policy on its anonymous token policy. Because channels renew about hourly, untrusting a server or the expiry of its certificate drops a live connection.
- A rejected server certificate is written to `{CertificateStoreBasePath}/rejected/certs` (the SDK keeps the last five). Moving it to `{CertificateStoreBasePath}/trusted/certs`, or provisioning that store up front, makes the client trust the server.
- The client gets an issuer store, `{CertificateStoreBasePath}/issuer`, so a server certificate issued by a CA can be trusted through its CA certificate.
- `AddAppCertToTrustedStore` becomes `false` on the client, as on the server. The client has no reason to trust its own certificate, and with a shared store it would make an in-process server trust the client implicitly.

### Client certificate

- The certificate step runs after endpoint selection, whenever the selected endpoint is signed or encrypted. Discovery needs no client certificate. With an unsecured endpoint nothing is created, loaded or written.
- `AutoCreateApplicationCertificate`, default `true`. Both modes call `ApplicationInstance.CheckApplicationInstanceCertificatesAsync(silent: true)`, which loads the certificate from `{CertificateStoreBasePath}/own` and validates its application URI and key size.
  - On: a missing certificate is created, self-signed.
  - Off: `ApplicationInstance.DisableCertificateAutoCreation` is set, so a missing certificate fails with an SDK error naming the store. An existing certificate whose key cannot be read fails with "Cannot access private key", distinct from a missing one.
- `ApplicationUri`, default `null`, which keeps today's `urn:{host}:Namotion.Interceptor:{ApplicationName}`. A host name change, for example on every recreate of a container, otherwise makes the SDK reject the existing certificate on every attempt without replacing it (`ApplicationInstance.cs:684-708`). A provisioned certificate must carry this URI and the subject `CN={ApplicationName}, O=Namotion`; the docs state both.
- `ApplicationCertificateLifetimeInMonths`, default `12`, passed to the certificate creation.
- Renewal: when `AutoCreateApplicationCertificate` is on and the loaded certificate is self-signed, a certificate with fewer than 30 days left or already expired is replaced by a new one, logged as a warning because the server has to trust the new certificate. A CA-issued certificate is never renewed, and with auto-creation off nothing is ever replaced. Without renewal the SDK keeps loading an expired certificate and only logs a warning (`ApplicationInstance.cs:600-617`).
- A certificate that is already loaded with its private key, for example set by a `CreateApplicationInstanceAsync` override, is not loaded, renewed or replaced. This guard is load-bearing: loading overwrites the in-memory certificate, even with null (`CertificateIdentifier.cs:211-217`).

### Loading once, serialized

- Each client source loads the certificate on its first secured connect and keeps it. Later connect attempts and manual reconnects set it on the new application instance, so the guard applies and the store is not read again. The renewal check runs on each attempt against the kept certificate, without disk access while the certificate is outside the renewal window; inside it, each attempt retries the renewal under the lock. A certificate replaced on disk by someone else is picked up after a restart of the source.
- Loading, creating and renewing run under a process-wide lock keyed by the full path of the own store, so two sources sharing a store and an application name cannot create, and then delete, each other's certificates on first run. Separate processes sharing a store should provision the certificate up front or use distinct names or paths; the docs say so.
- When loading or renewing fails while the current certificate has not expired, it stays in use, the error is logged and the next connect retries; once the certificate has expired, the failure propagates.
- SDK reconnects reuse the session's loaded certificate and never run the step.

### Customization

- `CreateApplicationInstanceAsync` stays virtual and is the one extension point.
  - Password-protected keys: set `SecurityConfiguration.CertificatePasswordProvider`.
  - Other store types such as the Windows certificate store: set the store type of the application certificate identifier.
  - Hardware security modules or any external source: set `ApplicationCertificate.Certificate` with its private key; the guard then leaves it alone.
- An override that changes security settings after calling the base must call `config.CertificateValidator.UpdateAsync(config)` again; the docs example is fixed accordingly.

### Server

- `OpcUaServerConfiguration.CleanCertificateStore` defaults to `false`, so a restart keeps the server certificate and clients keep trusting it.

### Errors

- A certificate step failure and a rejected server certificate are wrapped in a message naming the store paths (with `certs/`), the thumbprint and the option to change, like the existing discovery wrapper in `SessionManager.CreateSessionAsync`.
- They go through the existing failure paths: a failed initial connect retries after `RetryTime`, a failed manual reconnect on the next health check tick, and both are reported in `Diagnostics.LastError`.

## Breaking changes

- `UseSecurity` is renamed to `RequireSecurity`, and `true` now fails instead of downgrading when the server has no secured endpoint. Migration: rename the property. `UseSecurity = true` could not connect before, so no working setup relied on it.
- The client validates server certificates by default. Migration: trust the server certificate through `{CertificateStoreBasePath}/trusted/certs`, or set `AutoAcceptUntrustedCertificates = true` for the previous behavior.
- The server keeps its certificate across restarts. Migration: set `CleanCertificateStore = true` for the previous behavior.

## Tests

- Unit, against a temporary certificate store:
  - `AutoAcceptUntrustedCertificates` reaches `CertificateValidator.AutoAcceptUntrustedCertificates`.
  - Auto-create on: the first load creates a certificate with the configured lifetime and loads it with its private key; a second source reuses it.
  - Auto-create off: a missing certificate fails with the store named; an unreadable key fails differently.
  - A certificate with another application URI fails as described, and passes once `ApplicationUri` matches.
  - A self-signed certificate close to expiry is renewed; a CA-issued one is not; with auto-creation off nothing is renewed.
  - A failed renewal keeps the valid certificate, and the next attempt renews it.
  - A damaged store keeps the loaded valid certificate.
  - A failed renewal of an expired certificate fails.
  - An already loaded certificate is not replaced, on the first connect and on a manual reconnect.
  - Two sources creating concurrently end with one certificate.
  - A password-protected certificate loads through `CertificatePasswordProvider`.
  - With an unsecured endpoint, nothing is written to the store.
- Integration:
  - A secured connection works with the library's own certificate step; the test client's certificate-loading subclass is removed.
  - With strict trust, an untrusted server is rejected, the failure shows in `Diagnostics.LastError` and the certificate appears in `rejected/certs` (polled, the write is asynchronous); after moving it to `trusted/certs`, the client connects. The test uses a short `RetryTime`.
  - A username login over an unsecured channel is rejected with strict trust and works once the server is trusted. The server advertises a username policy, and the client checks the certificate before sending the token.
  - `RequireSecurity = true` against a server with only unsecured endpoints fails; `false` against a server with only secured endpoints connects.
  - A server restart keeps its certificate, and a strict client reconnects without trusting it again.
  - The subscription transfer test sets `AutoAcceptUntrustedCertificates = true`, since it tests transfers, not trust.
- The public API snapshot is updated.

## Docs

- `docs/connectors-opcua-client.md`, Transport Security: `RequireSecurity`, where the client certificate lives, its subject and application URI, creation and renewal, the trust workflow on both sides through the `rejected`, `trusted` and `issuer` stores, the customization recipes, separate processes sharing a store, and the migration notes.
- The client configuration table: the new and renamed options.
- `docs/connectors-opcua-server.md`: the new `CleanCertificateStore` default.
