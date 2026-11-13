using HomeBlaze.Services;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Virtual;

/// <summary>What the subjects of one virtual subject share to materialize and compute their properties.</summary>
internal sealed record VirtualRuntime(SubjectPathResolver Resolver, VirtualPropertyAwaiter Awaiter, ILogger Logger);
