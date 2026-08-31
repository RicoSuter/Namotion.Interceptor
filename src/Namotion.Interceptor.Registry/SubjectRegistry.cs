using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry;

/// <summary>Registers subjects and their property edges as they enter the object graph.</summary>
/// <remarks>
/// Runs before <see cref="LifecycleInterceptor"/>'s handler slot, so a subject is registered before
/// traversal reaches its children. Registry is a revisioned projection only; it does not participate
/// in ownership or reachability.
/// </remarks>
[RunsBefore(typeof(LifecycleInterceptor))]
public class SubjectRegistry : ISubjectRegistry, ISubjectIdRegistry, ISubjectIdRegistryWriter,
    ILifecycleHandler, IPropertyLifecycleHandler
{
    private sealed class ProjectionRevision
    {
        internal long Attachment;
        internal long Parents;
    }

    private readonly Dictionary<IInterceptorSubject, RegisteredSubject> _knownSubjects =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IInterceptorSubject> _subjectIdToSubject = new();
    private readonly ConditionalWeakTable<IInterceptorSubject, ProjectionRevision> _subjectRevisions = new();
    private ImmutableDictionary<IInterceptorSubject, RegisteredSubject>? _knownSubjectsSnapshot;

    /// <inheritdoc />
    public IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _knownSubjectsSnapshot) ?? GetKnownSubjectsSlow();
    }

    private ImmutableDictionary<IInterceptorSubject, RegisteredSubject> GetKnownSubjectsSlow()
    {
        lock (_knownSubjects)
        {
            var snapshot = _knownSubjectsSnapshot;
            if (snapshot is null)
            {
                snapshot = _knownSubjects.ToImmutableDictionary(ReferenceEqualityComparer.Instance);
                Volatile.Write(ref _knownSubjectsSnapshot, snapshot);
            }

            return snapshot;
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubject? TryGetRegisteredSubject(IInterceptorSubject subject)
    {
        lock (_knownSubjects)
        {
            return _knownSubjects.GetValueOrDefault(subject);
        }
    }

    /// <inheritdoc />
    string ISubjectIdRegistryWriter.GetOrAddSubjectId(IInterceptorSubject subject)
    {
        lock (_knownSubjects)
        {
            var existing = subject.TryGetSubjectId();
            if (existing is not null)
            {
                return existing;
            }

            var id = SubjectRegistryExtensions.GenerateSubjectId();
            SubjectRegistryExtensions.HasSubjectIds = true;
            subject.Data[(null, SubjectRegistryExtensions.SubjectIdKey)] = id;
            if (_knownSubjects.ContainsKey(subject))
            {
                _subjectIdToSubject[id] = subject;
            }

            return id;
        }
    }

    /// <inheritdoc />
    void ISubjectIdRegistryWriter.SetSubjectId(IInterceptorSubject subject, string id)
    {
        lock (_knownSubjects)
        {
            if (_subjectIdToSubject.TryGetValue(id, out var existing) && !ReferenceEquals(existing, subject))
            {
                throw new InvalidOperationException($"Subject ID '{id}' is already in use by a different subject.");
            }

            var oldId = subject.TryGetSubjectId();
            if (oldId is not null && oldId != id)
            {
                throw new InvalidOperationException($"Subject already has ID '{oldId}'; cannot reassign to '{id}'.");
            }

            SubjectRegistryExtensions.HasSubjectIds = true;
            subject.Data[(null, SubjectRegistryExtensions.SubjectIdKey)] = id;
            if (_knownSubjects.ContainsKey(subject))
            {
                _subjectIdToSubject[id] = subject;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGetSubjectById(string subjectId, out IInterceptorSubject subject)
    {
        lock (_knownSubjects)
        {
            return _subjectIdToSubject.TryGetValue(subjectId, out subject!);
        }
    }

    void ILifecycleHandler.HandleLifecycleChange(SubjectLifecycleChange change)
    {
        // Subject data belongs to the component boundary. Capture it before taking Registry's lock.
        var subjectId = change.IsContextAttach || change.IsContextDetach
            ? change.Subject.TryGetSubjectId()
            : null;
        var properties = change.IsContextDetach
            ? ImmutableArray<RegisteredSubject.PropertyProjection>.Empty
            : RegisteredSubject.CaptureProperties(change.Properties);

        lock (_knownSubjects)
        {
            var registeredSubject = _knownSubjects.GetValueOrDefault(change.Subject);
            if (registeredSubject is null &&
                change.IsPropertyReferenceAdded &&
                change.Context is not null &&
                IsNewerThanSubjectProjection(change.Subject, change.Revision))
            {
                registeredSubject = CreateProvisionalSubjectLocked(
                    change.Subject,
                    change.Context,
                    properties,
                    change.Parents,
                    change.Revision);
            }

            var attachmentAdvanced = false;
            if ((change.IsContextAttach || change.IsContextDetach) &&
                TryAdvanceAttachment(change.Subject, change.Revision))
            {
                attachmentAdvanced = true;
                if (change.IsContextAttach)
                {
                    if (registeredSubject is { AttachmentRevision: 0 } &&
                        ReferenceEquals(registeredSubject.Context, change.Context))
                    {
                        registeredSubject.CompleteAttachment(change.Revision);
                    }
                    else
                    {
                        registeredSubject = new RegisteredSubject(
                            change.Subject, properties, change.Context, change.Revision);
                        _knownSubjects[change.Subject] = registeredSubject;
                        Volatile.Write(ref _knownSubjectsSnapshot, null);
                    }

                    if (subjectId is not null &&
                        (!_subjectIdToSubject.TryGetValue(subjectId, out var existing) ||
                         ReferenceEquals(existing, change.Subject)))
                    {
                        _subjectIdToSubject[subjectId] = change.Subject;
                    }
                }

                if (registeredSubject is not null && ReferenceEquals(registeredSubject.Context, change.Context))
                {
                    if (change.IsContextDetach)
                    {
                        _knownSubjects.Remove(change.Subject);
                        Volatile.Write(ref _knownSubjectsSnapshot, null);
                        if (subjectId is not null &&
                            _subjectIdToSubject.TryGetValue(subjectId, out var mapped) &&
                            ReferenceEquals(mapped, change.Subject))
                        {
                            _subjectIdToSubject.Remove(subjectId);
                        }
                    }
                }
            }

            if ((!change.IsContextAttach && !change.IsContextDetach || attachmentAdvanced && !change.IsContextDetach) &&
                registeredSubject is not null &&
                ReferenceEquals(registeredSubject.Context, change.Context) &&
                TryAdvanceParents(change.Subject, change.Revision))
            {
                registeredSubject.ApplyProjection(
                    properties,
                    ResolveParents(change.Parents, ignoreDetachedParents: change.IsPropertyReferenceRemoved));
            }

            if (change.Property is { } parentProperty &&
                TryGetRegisteredPropertyLocked(parentProperty) is { } registeredProperty)
            {
                registeredProperty.TryReplaceChildren(change.Revision, change.PropertyChildren);
            }
        }
    }

    void IPropertyLifecycleHandler.AttachProperty(SubjectPropertyLifecycleChange change)
    {
        var projection = RegisteredSubject.CaptureProperty(change.Metadata);
        var needsProvisionalSubject = false;
        lock (_knownSubjects)
        {
            needsProvisionalSubject = _knownSubjects.GetValueOrDefault(change.Subject) is null;
        }

        var provisionalProperties = needsProvisionalSubject
            ? RegisteredSubject.CaptureProperties([.. change.Subject.Properties.Values])
            : [];
        var provisionalParents = needsProvisionalSubject
            ? change.Subject.GetParents()
            : [];

        RegisteredSubjectProperty? property;
        lock (_knownSubjects)
        {
            if (_knownSubjects.GetValueOrDefault(change.Subject) is null &&
                needsProvisionalSubject &&
                change.Context is not null &&
                IsNewerThanSubjectProjection(change.Subject, change.Revision))
            {
                CreateProvisionalSubjectLocked(
                    change.Subject,
                    change.Context,
                    provisionalProperties,
                    provisionalParents,
                    change.Revision);
            }

            property = ApplyPropertyProjection(change, projection);
        }

        if (property is not null)
        {
            RunInitializers(change, property);
        }
    }

    void IPropertyLifecycleHandler.DetachProperty(SubjectPropertyLifecycleChange change)
    {
        var projection = RegisteredSubject.CaptureProperty(change.Metadata);
        lock (_knownSubjects)
        {
            ApplyPropertyProjection(change, projection);
        }
    }

    void IPropertyLifecycleHandler.RefreshCollectionProperty(SubjectPropertyLifecycleChange change)
    {
        var projection = RegisteredSubject.CaptureProperty(change.Metadata);
        lock (_knownSubjects)
        {
            ApplyPropertyProjection(change, projection);
        }
    }

    private RegisteredSubjectProperty? ApplyPropertyProjection(
        SubjectPropertyLifecycleChange change,
        RegisteredSubject.PropertyProjection projection)
    {
        var registeredSubject = _knownSubjects.GetValueOrDefault(change.Subject);
        if (registeredSubject is null ||
            !ReferenceEquals(registeredSubject.Context, change.Context) ||
            change.Revision > 0 && change.Revision < registeredSubject.AttachmentRevision)
        {
            return null;
        }

        var property = registeredSubject.GetOrAddPropertyProjection(
            projection.Name, projection.Type, projection.Attributes);
        if (change.Revision == 0)
        {
            return property;
        }

        property.TryReplaceChildren(change.Revision, change.Children);

        foreach (var childProjection in change.ChildSubjects)
        {
            var child = _knownSubjects.GetValueOrDefault(childProjection.Subject);
            if (child is not null &&
                ReferenceEquals(child.Context, change.Context) &&
                TryAdvanceParents(childProjection.Subject, change.Revision))
            {
                child.ReplaceParents(ResolveParents(childProjection.Parents));
            }
        }

        return property;
    }

    private void RunInitializers(
        SubjectPropertyLifecycleChange change,
        RegisteredSubjectProperty property)
    {
        List<Exception>? failures = null;
        foreach (var initializer in property.ReflectionAttributes.OfType<ISubjectPropertyInitializer>())
        {
            TryInitialize(initializer, property, ref failures);
        }

        if (change.Context is not null)
        {
            foreach (var initializer in change.Context.GetServices<ISubjectPropertyInitializer>())
            {
                TryInitialize(initializer, property, ref failures);
            }
        }

        if (failures is { Count: 1 })
        {
            throw failures[0];
        }
        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }

    private static void TryInitialize(
        ISubjectPropertyInitializer initializer,
        RegisteredSubjectProperty property,
        ref List<Exception>? failures)
    {
        try
        {
            initializer.InitializeProperty(property);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
    }

    private bool TryAdvanceAttachment(IInterceptorSubject subject, long revision)
    {
        var projection = _subjectRevisions.GetValue(subject, static _ => new ProjectionRevision());
        if (revision <= projection.Attachment)
        {
            return false;
        }

        projection.Attachment = revision;
        return true;
    }

    private RegisteredSubject CreateProvisionalSubjectLocked(
        IInterceptorSubject subject,
        IInterceptorSubjectContext context,
        ImmutableArray<RegisteredSubject.PropertyProjection> properties,
        ImmutableArray<SubjectParent> parents,
        long revision)
    {
        var provisionalSubject = new RegisteredSubject(subject, properties, context, 0);
        _knownSubjects[subject] = provisionalSubject;
        Volatile.Write(ref _knownSubjectsSnapshot, null);
        try
        {
            provisionalSubject.ApplyProjection(properties, ResolveParents(parents));
            TryAdvanceParents(subject, revision);
            return provisionalSubject;
        }
        catch
        {
            _knownSubjects.Remove(subject);
            Volatile.Write(ref _knownSubjectsSnapshot, null);
            throw;
        }
    }

    private bool TryAdvanceParents(IInterceptorSubject subject, long revision)
    {
        var projection = _subjectRevisions.GetValue(subject, static _ => new ProjectionRevision());
        if (revision <= projection.Parents)
        {
            return false;
        }

        projection.Parents = revision;
        return true;
    }

    private bool IsNewerThanSubjectProjection(IInterceptorSubject subject, long revision)
    {
        var projection = _subjectRevisions.GetValue(subject, static _ => new ProjectionRevision());
        return revision > projection.Attachment;
    }

    private ImmutableArray<SubjectPropertyParent> ResolveParents(
        ImmutableArray<SubjectParent> parents,
        bool ignoreDetachedParents = false)
    {
        if (parents.IsEmpty)
        {
            return [];
        }

        var projection = ImmutableArray.CreateBuilder<SubjectPropertyParent>(parents.Length);
        foreach (var parent in parents)
        {
            var property = TryGetRegisteredPropertyLocked(parent.Property);
            if (property is null)
            {
                if (ignoreDetachedParents && !_knownSubjects.ContainsKey(parent.Property.Subject))
                {
                    continue;
                }

                throw new InvalidOperationException($"Property '{parent.Property.Name}' is not registered.");
            }

            projection.Add(new SubjectPropertyParent { Property = property, Index = parent.Index });
        }

        return ignoreDetachedParents
            ? projection.ToImmutable()
            : projection.MoveToImmutable();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RegisteredSubjectProperty? TryGetRegisteredPropertyLocked(PropertyReference property) =>
        _knownSubjects.GetValueOrDefault(property.Subject)?.TryGetProperty(property.Name);
}
