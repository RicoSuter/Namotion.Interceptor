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

builder.AddProject<Projects.HomeBlaze>("homeblaze")
    .WithReference(seq)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// OPC UA simulator for trying the OPC UA client without hardware: opc.tcp://localhost:50000
builder.AddContainer("opcplc", "iotedge/opc-plc")
    .WithImageRegistry("mcr.microsoft.com")
    // The OPC UA stack replaces "localhost" in the advertised endpoint with the machine name, so the container's
    // own hostname must be localhost for clients on the host to reach the endpoint it advertises.
    .WithContainerRuntimeArgs("--hostname", "localhost")
    .WithArgs("--pn=50000", "--autoaccept", "--ut", "--ph=localhost")
    .WithEndpoint(port: 50000, targetPort: 50000, scheme: "tcp", name: "opcua")
    .WithLifetime(containerLifetime);

builder.Build().Run();
