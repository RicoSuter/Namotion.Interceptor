using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

internal static class SupportContractAssertions
{
    public static void Settled(IInterceptorSubjectContext context, IInterceptorSubject[] roots, params IInterceptorSubject[] subjects)
    {
        var errors = CompareStorageGraph(context, roots, subjects, "settled");
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        foreach (var root in roots)
            Assert.NotEqual(SubjectAttachmentAnchorKind.None, root.Executor.AttachmentAnchor);
    }

    public static List<string> CompareStorageGraph(
        IInterceptorSubjectContext context,
        IInterceptorSubject[] roots,
        IInterceptorSubject[] universe,
        string phase)
    {
        return new StorageGraphComparison(context, universe, phase).Compare(roots);
    }

    /// <summary>
    /// Walks the stored values from the roots and compares what they reach with the ownership graph,
    /// the parent projections and the Registry, collecting one error per difference.
    /// </summary>
    private sealed class StorageGraphComparison(IInterceptorSubjectContext context, IInterceptorSubject[] universe, string phase)
    {
        private readonly List<string> _errors = [];
        private readonly HashSet<IInterceptorSubject> _reachable = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IInterceptorSubject, List<string>> _incoming = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<PropertyReference, List<string>> _outgoing = new(PropertyReference.Comparer);
        private readonly Dictionary<PropertyReference, object?> _storedValues = new(PropertyReference.Comparer);
        private readonly Stack<IInterceptorSubject> _pending = new();
        private readonly OwnershipGraph _graph = context.TryGetLifecycleInterceptor()!.Graph;
        private readonly ISubjectRegistry _registry = context.GetService<ISubjectRegistry>();

        public List<string> Compare(IInterceptorSubject[] roots)
        {
            foreach (var root in roots)
            {
                _pending.Push(root);
            }

            WalkStorage();
            CompareBaselines();
            foreach (var subject in universe)
            {
                CompareSubject(subject);
            }

            EqualEdges(_reachable.Select(Identity), _registry.KnownSubjects.Keys.Select(Identity), "complete Registry membership");
            return _errors;
        }

        private void WalkStorage()
        {
            while (_pending.TryPop(out var subject))
            {
                if (!_reachable.Add(subject)) continue;
                foreach (var entry in subject.Properties)
                {
                    if (!entry.Value.IsIntercepted || !entry.Value.Type.CanContainSubjects()) continue;
                    var property = new PropertyReference(subject, entry.Key);
                    _outgoing[property] = [];
                    var value = entry.Value.GetValue?.Invoke(subject);
                    _storedValues.Add(property, value);
                    CollectValue(property, value);
                }
            }
        }

        private void CollectValue(PropertyReference property, object? value)
        {
            if (value is IInterceptorSubject child)
            {
                Collect(property, child, null);
                return;
            }

            if (value is not IEnumerable sequence || value is string) return;
            var index = 0;
            foreach (var item in sequence)
            {
                if (item is IInterceptorSubject element) Collect(property, element, index);
                index++;
            }
        }

        private void Collect(PropertyReference property, IInterceptorSubject child, object? index)
        {
            if (!_incoming.TryGetValue(child, out var parents))
            {
                parents = [];
                _incoming.Add(child, parents);
            }
            parents.Add(Edge(property, index));
            _outgoing[property].Add(Child(child, index));
            _pending.Push(child);
        }

        private void CompareBaselines()
        {
            foreach (var entry in _storedValues)
            {
                if (!_graph.HasBaseline(entry.Key) || !Equals(entry.Value, _graph.GetBaselineSnapshot(entry.Key).Value))
                    _errors.Add($"{phase}: {Edge(entry.Key, null)} baseline differs from storage");
            }
        }

        private void CompareSubject(IInterceptorSubject subject)
        {
            var shouldBeOwned = _reachable.Contains(subject);
            if (_graph.IsOwned(subject) != shouldBeOwned)
                _errors.Add($"{phase}: {Identity(subject)} ownership should be {shouldBeOwned}");
            if (ReferenceEquals(subject.TryGetContext(), context) != shouldBeOwned)
                _errors.Add($"{phase}: {Identity(subject)} context attachment should be {shouldBeOwned}");
            var expectedParents = _incoming.GetValueOrDefault(subject) ?? [];
            if (subject.GetReferenceCount() != expectedParents.Count)
                _errors.Add($"{phase}: {Identity(subject)} expected {expectedParents.Count} references, actual {subject.GetReferenceCount()}");
            EqualEdges(expectedParents, subject.GetParents().Select(parent => Edge(parent.Property, parent.Index)), Identity(subject) + " parents");
            var registered = _registry.TryGetRegisteredSubject(subject);
            if ((registered is not null) != shouldBeOwned)
                _errors.Add($"{phase}: {Identity(subject)} Registry membership should be {shouldBeOwned}");
            if (registered is not null)
                CompareRegistration(subject, registered, expectedParents);
            if (!shouldBeOwned)
                CompareReleased(subject);
        }

        private void CompareRegistration(IInterceptorSubject subject, RegisteredSubject registered, List<string> expectedParents)
        {
            EqualEdges(expectedParents, registered.Parents.Select(parent => Edge(parent.Property.Reference, parent.Index)), Identity(subject) + " Registry parents");
            foreach (var property in registered.Properties)
            {
                if (!property.CanContainSubjects) continue;
                EqualEdges(_outgoing.GetValueOrDefault(property.Reference) ?? [],
                    property.Children.Select(child => Child(child.Subject, child.Index)), Edge(property.Reference, null) + " Registry children");
            }
        }

        private void CompareReleased(IInterceptorSubject subject)
        {
            foreach (var name in subject.Properties.Keys)
            {
                if (_graph.HasBaseline(new PropertyReference(subject, name)))
                    _errors.Add($"{phase}: {Identity(subject)} has a released baseline for {name}");
            }
            if (subject.Executor.AttachmentAnchor != SubjectAttachmentAnchorKind.None || _graph.IsReleasing(subject))
                _errors.Add($"{phase}: {Identity(subject)} retains an anchor or release marker");
        }

        private void EqualEdges(IEnumerable<string> expected, IEnumerable<string> actual, string label)
        {
            var expectedEdges = expected.Order().ToArray();
            var actualEdges = actual.Order().ToArray();
            if (!expectedEdges.SequenceEqual(actualEdges))
                _errors.Add($"{phase}: {label}; expected [{string.Join(", ", expectedEdges)}], actual [{string.Join(", ", actualEdges)}]");
        }

        private string Identity(IInterceptorSubject subject)
        {
            var index = Array.FindIndex(universe, candidate => ReferenceEquals(candidate, subject));
            if (index < 0)
            {
                var identity = $"unexpected subject ({subject.GetType().Name})";
                _errors.Add($"{phase}: encountered a subject absent from the fixture universe: {identity}");
                return identity;
            }
            return $"subject[{index}]";
        }

        private string Edge(PropertyReference property, object? index) => $"{Identity(property.Subject)}.{property.Name}[{index ?? "-"}]";

        private string Child(IInterceptorSubject subject, object? index) => $"{Identity(subject)}[{index ?? "-"}]";
    }

    public static void AssertAttached(IInterceptorSubjectContext context, IInterceptorSubject subject, int references)
    {
        Assert.Same(context, subject.TryGetContext());
        Assert.True(context.TryGetLifecycleInterceptor()!.Graph.IsOwned(subject));
        Assert.Equal(references, subject.GetReferenceCount());
        Assert.Equal(references, subject.GetParents().Length);
        var registered = context.GetService<ISubjectRegistry>().TryGetRegisteredSubject(subject);
        Assert.NotNull(registered);
        Assert.Equal(references, registered.Parents.Length);
    }

    public static void AssertReleased(IInterceptorSubjectContext context, params IInterceptorSubject[] subjects)
    {
        var graph = context.TryGetLifecycleInterceptor()!.Graph;
        var registry = context.GetService<ISubjectRegistry>();
        foreach (var subject in subjects)
        {
            Assert.Null(subject.TryGetContext());
            Assert.False(graph.IsOwned(subject));
            Assert.False(graph.IsReleasing(subject));
            Assert.Equal(SubjectAttachmentAnchorKind.None, subject.Executor.AttachmentAnchor);
            Assert.Equal(0, subject.GetReferenceCount());
            Assert.Empty(subject.GetParents());
            Assert.Null(registry.TryGetRegisteredSubject(subject));
            foreach (var property in subject.Properties.Keys)
                Assert.False(graph.HasBaseline(new PropertyReference(subject, property)));
        }
    }
}
