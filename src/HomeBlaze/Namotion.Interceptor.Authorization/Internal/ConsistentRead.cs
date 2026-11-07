using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>
/// Reads the graph as of one instant through the registry's structure version: an attempt starts at an even version
/// and is accepted only when the version is the same afterwards, so the attempt overlapped no registry change, not
/// even part of one. A mutable struct, kept in a local and used by one evaluation.
/// </summary>
internal struct ConsistentRead
{
    private readonly ISubjectRegistry _registry;
    private SpinWait _spinner = default;
    private long _version;
    private int _attempts;

    public ConsistentRead(ISubjectRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>Starts an attempt at an even version; false when the attempts ran out and the caller fails closed.</summary>
    public bool TryBegin()
    {
        while (_attempts < Limits.MaxConsistencyAttempts)
        {
            _attempts++;
            _version = _registry.StructureVersion;
            if ((_version & 1) == 0)
            {
                return true;
            }

            // A registry change is in progress; reading now would only be discarded.
            _spinner.SpinOnce(sleep1Threshold: -1);
        }

        return false;
    }

    /// <summary>Whether no registry change overlapped the attempt; otherwise waits before the next one.</summary>
    public bool TryEnd()
    {
        // Keeps the second read of the version after every read of the attempt, also the reads the registry makes
        // under its lock, whose release does not order a later load.
        Interlocked.MemoryBarrier();
        if (_registry.StructureVersion == _version)
        {
            return true;
        }

        _spinner.SpinOnce(sleep1Threshold: -1);
        return false;
    }
}
