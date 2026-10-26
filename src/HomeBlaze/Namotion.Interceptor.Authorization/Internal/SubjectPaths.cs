using System.Collections.Immutable;
using System.Diagnostics;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>
/// Enumerates the paths of a subject from the registry's public API.
/// </summary>
internal static class SubjectPaths
{
    private const int ParentLinksPerPath = 64;
    private const int CycleDetectionDepthThreshold = 256;

    /// <summary>
    /// Gets every path of the subject: one per chain of parents from the subject up to a subject that has no parents,
    /// in the order the parents are listed. A path is its links from the root's property down to the property holding
    /// the subject, each in every spelling the path reader resolves. When the chain of first parents ends at a subject
    /// without parents and every property on it is included, the first path is the one the registry's
    /// <see cref="PathExtensions.TryGetPath(RegisteredSubjectProperty, PathProviderBase, IInterceptorSubject?, object?)"/>
    /// writes for the subject's first parent. A chain that returns to a subject already on it is not a path and is skipped.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="pathProvider">The path provider.</param>
    /// <param name="paths">Receives the paths that could be written, each as its links in root-to-subject order.</param>
    /// <param name="maxPaths">The most paths to write; positive.</param>
    /// <returns>
    /// True when every chain was written; false when a chain could not be written (a property on it is excluded or an
    /// [InlinePaths] key resolves neither bare nor in the explicit form, because another segment shadows it), more than
    /// <paramref name="maxPaths"/> chains exist, or the search took more than 64 parent links per allowed path before
    /// every chain was found. A subject without parents has no paths and returns true.
    /// </returns>
    public static bool TryGetPaths(RegisteredSubject subject, PathProviderBase pathProvider, List<PathLink[]> paths, int maxPaths)
    {
        Debug.Assert(maxPaths > 0, "maxPaths must be positive.");

        var parents = subject.Parents;
        if (parents.Length == 0)
        {
            return true;
        }

        var writer = new ChainWriter(pathProvider, paths, maxPaths);
        var frames = new List<SubjectPropertyParent>(8);
        if (!TryWriteSingleParentChain(subject, parents, frames, writer))
        {
            frames.Clear();
            WriteChains(subject, parents, (long)maxPaths * ParentLinksPerPath, frames, writer);
        }

        return writer.IsComplete;
    }

    /// <summary>
    /// Follows the only parent at every level without a search and writes the one chain. False at the first subject
    /// with several parents, where the full search has to take over.
    /// </summary>
    private static bool TryWriteSingleParentChain(
        RegisteredSubject subject, ImmutableArray<SubjectPropertyParent> parents, List<SubjectPropertyParent> frames, ChainWriter writer)
    {
        var current = subject;
        HashSet<RegisteredSubject>? visited = null;
        var depth = 0;
        while (parents.Length == 1)
        {
            // Start tracking only past the threshold; a revisit then means the chain is a cycle and has no path.
            if (++depth > CycleDetectionDepthThreshold && !(visited ??= []).Add(current))
            {
                return true;
            }

            var parent = parents[0];
            frames.Add(parent);
            current = parent.Property.Parent;
            parents = current.Parents;
            if (parents.Length == 0)
            {
                writer.TryWrite(frames);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Iterative depth-first search over every chain of parents from <paramref name="subject"/> to a subject without
    /// parents, handing each complete chain (leaf-first in <paramref name="frames"/>) to <paramref name="writer"/>.
    /// Cycles are pruned per branch. Owners are not memoized, because every chain is wanted, so the number of parent
    /// links taken is bounded by <paramref name="budget"/> instead.
    /// </summary>
    private static void WriteChains(
        RegisteredSubject subject, ImmutableArray<SubjectPropertyParent> parents, long budget,
        List<SubjectPropertyParent> frames, ChainWriter writer)
    {
        var visiting = new HashSet<RegisteredSubject> { subject };

        // stack[i + 1] expands the owner frames[i] leads into; stack[0] is the subject, which has no frame.
        var stack = new List<SearchFrame>(8) { new(subject, parents) };
        while (stack.Count > 0)
        {
            var topIndex = stack.Count - 1;
            var top = stack[topIndex];
            if (top.NextParentIndex == top.Parents.Length)
            {
                Pop(stack, frames, visiting);
            }
            else if (--budget < 0)
            {
                writer.MarkIncomplete();
                return;
            }
            else
            {
                stack[topIndex] = top with { NextParentIndex = top.NextParentIndex + 1 };
                if (!TryTake(top.Parents[top.NextParentIndex], stack, frames, visiting, writer))
                {
                    return;
                }
            }
        }
    }

    private static void Pop(List<SearchFrame> stack, List<SubjectPropertyParent> frames, HashSet<RegisteredSubject> visiting)
    {
        var topIndex = stack.Count - 1;
        visiting.Remove(stack[topIndex].Owner);
        stack.RemoveAt(topIndex);
        if (stack.Count > 0)
        {
            frames.RemoveAt(frames.Count - 1);
        }
    }

    /// <summary>
    /// Takes one parent link: writes the chain when it reaches a subject without parents, otherwise descends into the
    /// owner unless it is already on the branch. False when the result is settled and the search can stop.
    /// </summary>
    private static bool TryTake(
        SubjectPropertyParent parent, List<SearchFrame> stack, List<SubjectPropertyParent> frames,
        HashSet<RegisteredSubject> visiting, ChainWriter writer)
    {
        var owner = parent.Property.Parent;

        // Parents locks and returns a fresh copy per call, so it is read once per owner.
        var ownerParents = owner.Parents;
        if (ownerParents.Length == 0)
        {
            frames.Add(parent);
            var proceed = writer.TryWrite(frames);
            frames.RemoveAt(frames.Count - 1);
            return proceed;
        }

        if (visiting.Add(owner))
        {
            frames.Add(parent);
            stack.Add(new SearchFrame(owner, ownerParents));
        }

        return true;
    }

    private readonly record struct SearchFrame(RegisteredSubject Owner, ImmutableArray<SubjectPropertyParent> Parents, int NextParentIndex = 0);

    /// <summary>
    /// Collects the paths of complete chains and tracks whether every chain was written.
    /// </summary>
    private sealed class ChainWriter
    {
        private readonly PathProviderBase _pathProvider;
        private readonly char _separator;
        private readonly char _indexOpen;
        private readonly List<PathLink[]> _paths;
        private readonly int _maxPaths;
        private int _written;

        public ChainWriter(PathProviderBase pathProvider, List<PathLink[]> paths, int maxPaths)
        {
            _pathProvider = pathProvider;
            _separator = pathProvider.PathSeparator;
            _indexOpen = pathProvider.IndexOpen;
            _paths = paths;
            _maxPaths = maxPaths;
        }

        public bool IsComplete { get; private set; } = true;

        public void MarkIncomplete() => IsComplete = false;

        /// <summary>Writes the chain in <paramref name="frames"/> (leaf-first); false when the result is settled and the search can stop.</summary>
        public bool TryWrite(List<SubjectPropertyParent> frames)
        {
            if (_written == _maxPaths)
            {
                IsComplete = false;
                return false;
            }

            var path = TryGetLinks(frames);
            if (path is null)
            {
                IsComplete = false;
            }
            else
            {
                _paths.Add(path);
                _written++;
            }

            return IsComplete || _written < _maxPaths;
        }

        /// <summary>The links of the chain root first; null when a property on it is excluded or a link is unwritable.</summary>
        private PathLink[]? TryGetLinks(List<SubjectPropertyParent> frames)
        {
            // Resolution that respects IsPropertyIncluded checks every segment, so a chain through an excluded property has no path.
            foreach (var frame in frames)
            {
                if (!_pathProvider.IsPropertyIncluded(frame.Property))
                {
                    return null;
                }
            }

            var links = new PathLink[frames.Count];
            for (var i = 0; i < links.Length; i++)
            {
                var frame = frames[frames.Count - 1 - i];
                var link = GetLink(frame.Property, frame.Index);
                if (link.Segment is null && link.BareKey is null)
                {
                    return null;
                }

                links[i] = link;
            }

            return links;
        }

        // These link rules mirror the registry's path reader; SubjectPathsTests cross-checks them against it.
        private PathLink GetLink(RegisteredSubjectProperty property, object? index)
        {
            var ownSegment = MemberSegments.GetSegment(_pathProvider, property);
            if (index is null)
            {
                return new PathLink(ownSegment, null, null);
            }

            var text = PathExtensions.FormatPathIndex(index);
            if (!InlinePathsAttribute.IsInlinePathsProperty(property.Subject.GetType(), property.Name))
            {
                return new PathLink(ownSegment, text, null);
            }

            var bareKey = IsReadBare(property, text, ownSegment) ? text : null;

            // The explicit form only reads back while the own segment still resolves to this property.
            var segment = ResolvesTo(property, ownSegment) ? ownSegment : null;
            return new PathLink(segment, text, bareKey);
        }

        /// <summary>The reader takes the text as this key alone when it is one non-empty name that resolves to the property and is not its own segment.</summary>
        private bool IsReadBare(RegisteredSubjectProperty property, string text, string ownSegment)
            => text.Length > 0 &&
               text.AsSpan().IndexOfAny(_separator, _indexOpen) < 0 &&
               text != ownSegment &&
               ResolvesTo(property, text);

        private bool ResolvesTo(RegisteredSubjectProperty property, string segment)
            => _pathProvider.TryGetPropertyFromSegment(property.Parent, segment)?.Name == property.Name;
    }
}
