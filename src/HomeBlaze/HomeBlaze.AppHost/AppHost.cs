using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// A persistent container outlives the AppHost and is reused on the next start, so F5 does not wait for it every
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

builder.Build().Run();
