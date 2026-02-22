using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry;

public class SubjectRegistry : ISubjectRegistry, ISubjectIdRegistry, ISubjectIdRegistryWriter, ILifecycleHandler, IPropertyLifecycleHandler
{
    private readonly Dictionary<IInterceptorSubject, RegisteredSubject> _knownSubjects = new();
    private readonly Dictionary<string, IInterceptorSubject> _subjectIdToSubject = new();
    
    /// <inheritdoc />
    public IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects
    {
        get
        {
            lock (_knownSubjects)
                return _knownSubjects.ToImmutableDictionary();
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
                return existing;

            var id = SubjectRegistryExtensions.GenerateSubjectId();
            subject.Data[(null, SubjectRegistryExtensions.SubjectIdKey)] = id;
            _subjectIdToSubject[id] = subject;
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
                throw new InvalidOperationException(
                    $"Subject ID '{id}' is already in use by a different subject.");
            }

            var oldId = subject.TryGetSubjectId();
            if (oldId is not null && oldId != id)
            {
                _subjectIdToSubject.Remove(oldId);
            }

            subject.Data[(null, SubjectRegistryExtensions.SubjectIdKey)] = id;
            _subjectIdToSubject[id] = subject;
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

    /// <inheritdoc />
    void ILifecycleHandler.HandleLifecycleChange(SubjectLifecycleChange change)
    {
        lock (_knownSubjects)
        {
            if (change.IsContextAttach || change.IsPropertyReferenceAdded)
            {
                if (!_knownSubjects.TryGetValue(change.Subject, out var registeredSubject))
                {
                    registeredSubject = RegisterSubject(change.Subject);
                }

                if (change.IsContextAttach)
                {
                    // Auto-register subject ID in reverse index if the subject already has one
                    // (e.g., ID was set before attachment to a context with a registry)
                    var subjectId = change.Subject.TryGetSubjectId();
                    if (subjectId is not null)
                    {
                        _subjectIdToSubject[subjectId] = change.Subject;
                    }
                }

                if (change is { IsPropertyReferenceAdded: true, Property: { } property })
                {
                    if (!_knownSubjects.TryGetValue(property.Subject, out var parentRegisteredSubject))
                    {
                        parentRegisteredSubject = RegisterSubject(property.Subject);
                    }

                    var registeredProperty = parentRegisteredSubject.TryGetProperty(property.Name) ??
                        throw new InvalidOperationException($"Property '{property.Name}' not found.");

                    registeredSubject.AddParent(registeredProperty, change.Index);
                    registeredProperty.AddChild(new SubjectPropertyChild
                    {
                        Index = change.Index,
                        Subject = change.Subject,
                    });
                }

                return;
            }

            if (change.IsPropertyReferenceRemoved || change.IsContextDetach)
            {
                var registeredSubject = _knownSubjects.GetValueOrDefault(change.Subject);
                if (registeredSubject is not null)
                {
                    if (change is { IsPropertyReferenceRemoved: true, Property: not null })
                    {
                        var property = _knownSubjects
                            .GetValueOrDefault(change.Property.Value.Subject)?
                            .TryGetProperty(change.Property.Value.Name);

                        if (property is not null)
                        {
                            registeredSubject.RemoveParent(property, change.Index);
                         
                            property.RemoveChild(new SubjectPropertyChild
                            {
                                Subject = change.Subject,
                                Index = change.Index
                            });
                        }
                    }
                    
                    if (change.IsContextDetach)
                    {
                        _knownSubjects.Remove(change.Subject);

                        // Clean up subject ID reverse index via direct lookup (O(1))
                        if (_subjectIdToSubject.Count > 0)
                        {
                            var subjectId = change.Subject.TryGetSubjectId();
                            if (subjectId is not null)
                            {
                                _subjectIdToSubject.Remove(subjectId);
                            }
                        }
                    }
                }
            }
        }
    }

    void IPropertyLifecycleHandler.AttachProperty(SubjectPropertyLifecycleChange change)
    {
        var property = TryGetRegisteredProperty(change.Property);
        if (property is not null)
        {
            // handle property initializers from attributes
            foreach (var attribute in property.ReflectionAttributes.OfType<ISubjectPropertyInitializer>())
            {
                attribute.InitializeProperty(property);
            }

            // handle property initializers from context
            foreach (var initializer in change.Subject.Context.GetServices<ISubjectPropertyInitializer>())
            {
                initializer.InitializeProperty(property);
            }
        }
    }

    private RegisteredSubject RegisterSubject(IInterceptorSubject subject)
    {
        var registeredSubject = new RegisteredSubject(subject);
        _knownSubjects[subject] = registeredSubject;
        return registeredSubject;
    }

    void IPropertyLifecycleHandler.DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RegisteredSubjectProperty? TryGetRegisteredProperty(PropertyReference property)
    {
        return TryGetRegisteredSubject(property.Subject)?.TryGetProperty(property.Name);
    }
}
