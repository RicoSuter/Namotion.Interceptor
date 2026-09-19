using System.Buffers;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Cuts changes that are written at most a batch size at a time into slices, each written by one call. Every
/// subject-holding change goes into the first slice, which may therefore exceed the batch size: a receiver
/// completes a subject from the change that attaches it, and keeps a moved subject only when the change removing
/// it and the one adding it arrive together. Value changes fill the first slice up to the batch size and are
/// sliced by it after that. Each group keeps its order, and a property that changes more than once is merged by
/// commit revision first, since a receiver applies slices in their order.
/// </summary>
internal readonly struct SubjectChangeSlices : IDisposable
{
    // Create never awaits, so a set per thread is enough to keep the repeat check allocation-free.
    [ThreadStatic]
    private static HashSet<PropertyReference>? t_repeatScratch;

    private readonly ChangeMerger? _merger;
    private readonly SubjectPropertyChange[]? _orderedBuffer;
    private readonly int _firstSliceLength;
    private readonly int _batchSize;

    private SubjectChangeSlices(
        ReadOnlyMemory<SubjectPropertyChange> changes, int firstSliceLength, int batchSize,
        ChangeMerger? merger, SubjectPropertyChange[]? orderedBuffer)
    {
        Changes = changes;
        _firstSliceLength = firstSliceLength;
        _batchSize = batchSize;
        _merger = merger;
        _orderedBuffer = orderedBuffer;
    }

    /// <summary>The changes in slice order, valid until this instance is disposed.</summary>
    public ReadOnlyMemory<SubjectPropertyChange> Changes { get; }

    public static SubjectChangeSlices Create(ReadOnlyMemory<SubjectPropertyChange> changes, int batchSize)
    {
        ChangeMerger? merger = null;
        var repeatScratch = t_repeatScratch ??= new HashSet<PropertyReference>(PropertyReference.Comparer);
        if (ChangeMerger.HasRepeatedProperty(changes.Span, repeatScratch))
        {
            merger = new ChangeMerger();
            changes = merger.Merge(changes.Span);
        }

        var span = changes.Span;
        var subjectHoldingCount = 0;
        var isOrdered = true;
        for (var i = 0; i < span.Length; i++)
        {
            if (IsSubjectHolding(span[i]))
            {
                isOrdered &= subjectHoldingCount == i;
                subjectHoldingCount++;
            }
        }

        SubjectPropertyChange[]? orderedBuffer = null;
        if (!isOrdered)
        {
            orderedBuffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(span.Length);
            var orderedChanges = orderedBuffer.AsSpan(0, span.Length);
            var index = 0;
            foreach (var change in span)
            {
                if (IsSubjectHolding(change))
                {
                    orderedChanges[index++] = change;
                }
            }

            foreach (var change in span)
            {
                if (!IsSubjectHolding(change))
                {
                    orderedChanges[index++] = change;
                }
            }

            changes = new ReadOnlyMemory<SubjectPropertyChange>(orderedBuffer, 0, span.Length);
        }

        return new SubjectChangeSlices(changes, Math.Max(batchSize, subjectHoldingCount), batchSize, merger, orderedBuffer);
    }

    /// <summary>Gets the slice starting at <paramref name="start"/>, which is 0 or where the previous slice ended.</summary>
    public ReadOnlyMemory<SubjectPropertyChange> GetSlice(int start)
        => Changes.Slice(start, Math.Min(start == 0 ? _firstSliceLength : _batchSize, Changes.Length - start));

    public void Dispose()
    {
        if (_orderedBuffer is not null)
        {
            // Cleared because the changes hold subjects and values the pool must not keep alive.
            ArrayPool<SubjectPropertyChange>.Shared.Return(_orderedBuffer, clearArray: true);
        }

        _merger?.Dispose();
    }

    private static bool IsSubjectHolding(in SubjectPropertyChange change)
        => change.Property.Subject.Properties.TryGetValue(change.Property.Name, out var metadata) &&
           metadata.Type.CanContainSubjects();
}
