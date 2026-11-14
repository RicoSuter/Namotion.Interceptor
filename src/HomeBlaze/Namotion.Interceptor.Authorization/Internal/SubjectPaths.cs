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
    /// <summary>
    /// The segment of the property as the registry spells it when it builds a path: the provider's segment of an included
    /// property, or the browse name of one the provider excludes. An attribute is spelled the same way, so attribute
    /// paths follow the registry's syntax. A member that paths may not pass through still has a segment, which a deny
    /// rule can match.
    /// </summary>
    public static string GetSegment(PathProviderBase pathProvider, RegisteredSubjectProperty property)
        => pathProvider.TryGetPropertySegment(property, out var segment) ? segment : property.BrowseName;

    /// <summary>
    /// Gets every path of the subject: one per chain of parents from the subject up to a subject without parents, a
    /// root, in the order the parents are listed. Every subject a chain passes above the starting subject must be
    /// attached (<see cref="RegisteredSubject.IsAttached"/>); the starting subject is not checked, the caller checks
    /// it. A path is its links from the root's property down to the property holding the subject, each in every
    /// spelling the path reader resolves and in its structural spellings (see <see cref="PathLink"/>). A chain passes
    /// only properties the path provider effectively includes (see
    /// <see cref="PathProviderExtensions.IsPropertyEffectivelyIncluded"/>), as the registry's paths do, so a chain
    /// through an excluded property, or through an attribute of one, has no path. When the chain of first parents ends
    /// at a subject without parents and has a path, the first path is the one the registry's
    /// <see cref="PathExtensions.TryGetPath(RegisteredSubjectProperty, PathProviderBase, IInterceptorSubject?, object?)"/>
    /// builds for the subject's first parent. A chain that returns to a subject already on it is not a path and is
    /// skipped.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="pathProvider">The path provider.</param>
    /// <param name="paths">Receives the paths that could be built, each as its links in root-to-subject order, after the paths it already holds.</param>
    /// <param name="maxPaths">The most paths to build; positive.</param>
    /// <param name="followedSingleParents">True when every subject above the subject had a single parent, so the result came from following them without a search and without spending the search budget.</param>
    /// <returns>
    /// True when every chain was built; false when a chain has no path the registry could build or passes a subject
    /// that is no longer attached, more than <paramref name="maxPaths"/> chains exist, or the search took more than 64
    /// parent links per allowed path before every chain was found. A subject without parents has no paths and returns
    /// true.
    /// </returns>
    public static bool TryGetPaths(
        RegisteredSubject subject, PathProviderBase pathProvider, PathStore paths, int maxPaths, out bool followedSingleParents)
    {
        Debug.Assert(maxPaths > 0, "maxPaths must be positive.");

        followedSingleParents = true;
        var parents = subject.Parents;
        var builder = new ChainBuilder(pathProvider, paths, maxPaths);
        var frames = paths.Frames;
        frames.Clear();
        if (!TryBuildSingleParentChain(subject, parents, frames, ref builder))
        {
            followedSingleParents = false;
            frames.Clear();
            BuildChains(subject, parents, (long)maxPaths * Limits.ParentLinksPerPath, frames, ref builder);
        }

        return builder.IsComplete;
    }

    /// <summary>
    /// Follows the only parent at every level without a search and builds the one chain. False at the first subject
    /// with several parents, where the full search has to take over.
    /// </summary>
    private static bool TryBuildSingleParentChain(
        RegisteredSubject subject, ImmutableArray<SubjectPropertyParent> parents, List<SubjectPropertyParent> frames,
        ref ChainBuilder builder)
    {
        var current = subject;
        HashSet<RegisteredSubject>? visited = null;
        var depth = 0;
        while (parents.Length == 1)
        {
            // Start tracking only past the threshold; a revisit then means the chain is a cycle and has no path.
            if (++depth > Limits.ChainCycleTrackingDepth && !(visited ??= []).Add(current))
            {
                return true;
            }

            var parent = parents[0];
            frames.Add(parent);
            current = parent.Property.Parent;
            parents = current.Parents;
            var isAttached = current.IsAttached;
            if (!isAttached)
            {
                builder.MarkIncomplete();
                return true;
            }

            if (parents.Length == 0)
            {
                builder.TryBuild(frames);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Iterative depth-first search over every chain of parents from <paramref name="subject"/> to a subject without
    /// parents, handing each complete chain (leaf-first in <paramref name="frames"/>) to <paramref name="builder"/>.
    /// Cycles are pruned per branch. Owners are not memoized, because every chain is wanted, so the number of parent
    /// links taken is bounded by <paramref name="budget"/> instead.
    /// </summary>
    private static void BuildChains(
        RegisteredSubject subject, ImmutableArray<SubjectPropertyParent> parents, long budget,
        List<SubjectPropertyParent> frames, ref ChainBuilder builder)
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
                builder.MarkIncomplete();
                return;
            }
            else
            {
                stack[topIndex] = top with { NextParentIndex = top.NextParentIndex + 1 };
                if (!TryTake(top.Parents[top.NextParentIndex], stack, frames, visiting, ref builder))
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
    /// Takes one parent link: builds the chain when it reaches a subject without parents, otherwise descends into the
    /// owner unless it is already on the branch. False when the result is settled and the search can stop.
    /// </summary>
    private static bool TryTake(
        SubjectPropertyParent parent, List<SearchFrame> stack, List<SubjectPropertyParent> frames,
        HashSet<RegisteredSubject> visiting, ref ChainBuilder builder)
    {
        var owner = parent.Property.Parent;
        var ownerParents = owner.Parents;
        var isAttached = owner.IsAttached;
        if (!isAttached)
        {
            builder.MarkIncomplete();
            return true;
        }

        if (ownerParents.Length == 0)
        {
            frames.Add(parent);
            var proceed = builder.TryBuild(frames);
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
    /// Collects the paths of complete chains and tracks whether every chain was built. A mutable struct, so it is
    /// passed by reference.
    /// </summary>
    private struct ChainBuilder
    {
        private readonly PathProviderBase _pathProvider;
        private readonly char _separator;
        private readonly char _indexOpen;
        private readonly PathStore _paths;
        private readonly int _maxPaths;
        private int _built;

        public ChainBuilder(PathProviderBase pathProvider, PathStore paths, int maxPaths)
        {
            _pathProvider = pathProvider;
            _separator = pathProvider.PathSeparator;
            _indexOpen = pathProvider.IndexOpen;
            _paths = paths;
            _maxPaths = maxPaths;
            _built = 0;
            IsComplete = true;
        }

        public bool IsComplete { get; private set; }

        public void MarkIncomplete() => IsComplete = false;

        /// <summary>Builds the chain in <paramref name="frames"/> (leaf-first); false when the result is settled and the search can stop.</summary>
        public bool TryBuild(List<SubjectPropertyParent> frames)
        {
            if (_built == _maxPaths)
            {
                IsComplete = false;
                return false;
            }

            if (TryAddLinks(frames))
            {
                _built++;
            }
            else
            {
                IsComplete = false;
            }

            return IsComplete || _built < _maxPaths;
        }

        /// <summary>Adds the links of the chain root first as a path; false when paths may not pass a property on it or a link cannot be built.</summary>
        private readonly bool TryAddLinks(List<SubjectPropertyParent> frames)
        {
            // The registry builds and resolves no path through such a property, nor through an attribute of an excluded property.
            foreach (var frame in frames)
            {
                if (!_pathProvider.IsPropertyEffectivelyIncluded(frame.Property))
                {
                    return false;
                }
            }

            var start = _paths.BeginPath();
            for (var i = frames.Count - 1; i >= 0; i--)
            {
                var frame = frames[i];
                var link = GetLink(frame.Property, frame.Index);
                if (link.Segment is null && link.BareKey is null)
                {
                    _paths.DropPath(start);
                    return false;
                }

                _paths.AddLink(link);
            }

            _paths.EndPath(start);
            return true;
        }

        // These link rules mirror the registry's path reader; SubjectPathsTests cross-checks them against it.
        private readonly PathLink GetLink(RegisteredSubjectProperty property, object? index)
        {
            var ownSegment = GetSegment(_pathProvider, property);
            var text = index is null ? null : PathExtensions.FormatPathIndex(index);
            if (!InlinePathsAttribute.IsInlinePathsProperty(property.Subject.GetType(), property.Name))
            {
                return new PathLink(ownSegment, text);
            }

            // The own segment, alone or with the key, reads back only while it still resolves to this property. Deny
            // rules match the structural spellings whether or not a property shadows them.
            var segment = ResolvesTo(property, ownSegment) ? ownSegment : null;
            if (text is null)
            {
                return new PathLink(segment, null, null, ownSegment, null);
            }

            var structuralBareKey = IsSingleName(text, ownSegment) ? text : null;
            var bareKey = structuralBareKey is not null && ResolvesTo(property, text) ? text : null;
            return new PathLink(segment, text, bareKey, ownSegment, structuralBareKey);
        }

        /// <summary>
        /// Whether the reader can take the text as a key alone: one non-empty name that is not the property's own
        /// segment. It takes it only while the name also resolves to the property.
        /// </summary>
        private readonly bool IsSingleName(string text, string ownSegment)
            => text.Length > 0 &&
               text.AsSpan().IndexOfAny(_separator, _indexOpen) < 0 &&
               text != ownSegment;

        private readonly bool ResolvesTo(RegisteredSubjectProperty property, string segment)
            => _pathProvider.TryGetPropertyFromSegment(property.Parent, segment, out var resolved) && resolved.Name == property.Name;
    }
}
