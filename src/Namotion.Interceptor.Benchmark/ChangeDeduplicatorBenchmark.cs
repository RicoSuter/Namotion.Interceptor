using System;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Measures one flush cycle of the <see cref="ChangeDeduplicator"/>: collapse a batch, then reset the
/// pooled state, which is what <c>ChangeQueueProcessor</c> runs on every buffer tick. Reset is inside the
/// measured operation because it clears the whole rented buffer, so its cost scales with the largest batch
/// the deduplicator has seen and cannot be attributed to the collapse alone.
/// </summary>
[MemoryDiagnoser]
public class ChangeDeduplicatorBenchmark
{
    public enum BatchShape
    {
        /// <summary>
        /// One change per property, each on its own subject. The steady-state connector batch, where
        /// deduplication finds nothing to collapse and only pays for the bookkeeping.
        /// </summary>
        Distinct,

        /// <summary>
        /// Few properties with many commits each, the burst that deduplication exists for.
        /// </summary>
        Duplicated,

        /// <summary>
        /// The same burst built from changes that carry no revision, which forces every property onto the
        /// arrival-position fallback.
        /// </summary>
        DuplicatedWithoutRevisions
    }

    private const int ChangesPerPropertyWhenDuplicated = 8;

    private ChangeDeduplicator _deduplicator;
    private SubjectPropertyChange[] _changes;

    [Params(64, 512)]
    public int BatchSize;

    [Params(BatchShape.Distinct, BatchShape.Duplicated, BatchShape.DuplicatedWithoutRevisions)]
    public BatchShape Shape;

    [GlobalSetup]
    public void Setup()
    {
        _deduplicator = new ChangeDeduplicator();

        var distinctPropertyCount = Shape == BatchShape.Distinct
            ? BatchSize
            : Math.Max(1, BatchSize / ChangesPerPropertyWhenDuplicated);

        // One subject per property, mirroring a connector batch that spans many nodes of the graph.
        // Revisions are per subject, so a property's revisions never compete with another property's.
        var properties = new PropertyReference[distinctPropertyCount];
        for (var index = 0; index < distinctPropertyCount; index++)
        {
            properties[index] = new PropertyReference(new Tire(), nameof(Tire.Pressure));
        }

        // decimal is 16 bytes and reference-free, so both values go into inline storage and the batch
        // holds no boxed value per change. That keeps the measured allocations those of the collapse.
        var carriesRevisions = Shape != BatchShape.DuplicatedWithoutRevisions;
        var revisions = new long[distinctPropertyCount];

        _changes = new SubjectPropertyChange[BatchSize];
        for (var index = 0; index < BatchSize; index++)
        {
            // Round-robin, so a duplicated batch interleaves its properties instead of arriving in runs.
            var propertyIndex = index % distinctPropertyCount;
            var revision = ++revisions[propertyIndex];

            _changes[index] = SubjectPropertyChange.Create(
                properties[propertyIndex],
                ChangeOrigin.Local,
                DateTimeOffset.UtcNow,
                null,
                (decimal)revision,
                (decimal)revision + 1,
                carriesRevisions ? revision : 0);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _deduplicator.Dispose();
    }

    [Benchmark]
    public int DeduplicateAndReset()
    {
        var survivorCount = _deduplicator.Deduplicate(_changes).Length;
        _deduplicator.Reset();
        return survivorCount;
    }
}
