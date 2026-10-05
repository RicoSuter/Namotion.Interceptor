using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Persistent containers outlive the AppHost and are reused on the next start, so F5 does not wait for them every
// time. Switched on by both launch profiles; off by default.
var containerLifetime = builder.Configuration.GetValue("AppHost:PersistentContainers", false)
    ? ContainerLifetime.Persistent
    : ContainerLifetime.Session;

var seq = builder.AddSeq("seq")
    .WithDataVolume()
    .WithLifetime(containerLifetime);

var homeblaze = builder.AddProject<Projects.HomeBlaze>("homeblaze")
    .WithReference(seq)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// Generated once and kept in the AppHost's user secrets, so the persistent n8n volume stays decryptable.
var n8nEncryptionKey = builder.AddParameter(
    "n8n-encryption-key",
    new GenerateParameterDefault { MinLength = 32, Special = false },
    secret: true,
    persist: true);

builder.AddContainer("n8n", "n8nio/n8n")
    .WithHttpEndpoint(targetPort: 5678, name: "http")
    .WithVolume("homeblaze-n8n-data", "/home/node/.n8n")
    .WithEnvironment("N8N_ENCRYPTION_KEY", n8nEncryptionKey)
    .WithEnvironment("N8N_SECURE_COOKIE", "false")
    .WithReference(homeblaze.GetEndpoint("http"))
    .WithLifetime(containerLifetime);

// OPC UA simulator for trying the OPC UA client without hardware: opc.tcp://localhost:50000
builder.AddContainer("opcplc", "iotedge/opc-plc")
    .WithImageRegistry("mcr.microsoft.com")
    .WithArgs("--pn=50000", "--autoaccept", "--ut", "--ph=localhost")
    .WithEndpoint(port: 50000, targetPort: 50000, scheme: "tcp", name: "opcua")
    .WithLifetime(containerLifetime);

builder.Build().Run();
