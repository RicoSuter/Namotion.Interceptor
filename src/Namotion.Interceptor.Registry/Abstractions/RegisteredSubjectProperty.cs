using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Abstractions;

public class RegisteredSubjectProperty : RegisteredSubjectMember
{
    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, int>? _reusableCollectionPositions;

    private readonly List<SubjectPropertyChild> _children = [];
    private ImmutableArray<SubjectPropertyChild> _childrenCache;

    public RegisteredSubjectProperty(RegisteredSubject parent, string name,
        Type type, IReadOnlyCollection<Attribute> reflectionAttributes)
        : base(parent, name, reflectionAttributes)
    {
        Type = type;
        Reference = new PropertyReference(parent.Subject, name);
    }

    /// <summary>
    /// Gets the subject object this property belongs to.
    /// </summary>
    public IInterceptorSubject Subject => Reference.Subject;

    /// <summary>
    /// Gets the property reference.
    /// </summary>
    public PropertyReference Reference { get; }

    /// <summary>
    /// Gets the type of the property.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// Checks whether this property has child subjects, which can be either
    /// a subject reference, a collection of subjects, or a dictionary of subjects.
    /// </summary>
    public bool CanContainSubjects
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type.CanContainSubjects();
    }

    /// <summary>
    /// Gets a value indicating whether this property references another subject.
    /// </summary>
    public bool IsSubjectReference
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type.IsSubjectReferenceType();
    }

    /// <summary>
    /// Gets a value indicating whether this property references multiple subject with a collection.
    /// </summary>
    public bool IsSubjectCollection
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type.IsSubjectCollectionType();
    }

    /// <summary>
    /// Gets a value indicating whether this property references multiple subject with a dictionary.
    /// </summary>
    public bool IsSubjectDictionary
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type.IsSubjectDictionaryType();
    }

    /// <summary>
    /// Gets a value indicating whether the property has a getter.
    /// </summary>
    public bool HasGetter => Reference.Metadata.GetValue is not null;

    /// <summary>
    /// Gets a value indicating whether the property has a setter.
    /// </summary>
    public bool HasSetter => Reference.Metadata.SetValue is not null;

    /// <summary>
    /// Gets the current value of the property.
    /// </summary>
    /// <returns>The value.</returns>
    public object? GetValue()
    {
        return Reference.Metadata.GetValue?.Invoke(Subject);
    }

    /// <summary>
    /// Sets the value of the property.
    /// </summary>
    /// <param name="value">The value.</param>
    public void SetValue(object? value)
    {
        Reference.Metadata.SetValue?.Invoke(Subject, value);
    }

    /// <summary>
    /// Gets the collection or dictionary items of the property.
    /// Thread-safe: Lock on private readonly List ensures thread-safe access.
    /// Performance: Returns cached ImmutableArray - only rebuilds when invalidated.
    /// </summary>
    public ImmutableArray<SubjectPropertyChild> Children
    {
        get
        {
            lock (_children)
            {
                if (_childrenCache.IsDefault)
                {
                    _childrenCache = [.. _children];
                }

                return _childrenCache;
            }
        }
    }

    /// <summary>
    /// Adds an attribute to the property.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes of the attribute.</param>
    /// <returns>The created attribute property.</returns>
    public RegisteredSubjectAttribute AddAttribute<TProperty>(
        string name,
        Func<IInterceptorSubject, TProperty?>? getValue,
        Action<IInterceptorSubject, TProperty?>? setValue = null,
        params Attribute[] attributes)
    {
        return AddAttribute(name, typeof(TProperty),
            getValue is not null ? x => (TProperty)getValue(x)! : null,
            setValue is not null ? (x, y) => setValue(x, (TProperty)y!) : null,
            attributes);
    }

    /// <summary>
    /// Adds an attribute to the property.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes of the attribute.</param>
    /// <returns>The created attribute property.</returns>
    public RegisteredSubjectAttribute AddAttribute<TProperty>(
        string name,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?>? setValue = null,
        params Attribute[] attributes)
    {
        return AddAttribute(name, typeof(TProperty), getValue, setValue, attributes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator PropertyReference(RegisteredSubjectProperty property)
    {
        return property.Reference;
    }

    internal void ClearChildren()
    {
        lock (_children)
        {
            _children.Clear();
            _childrenCache = default;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddChild(SubjectPropertyChild child)
    {
        lock (_children)
        {
            // No Contains check needed - LifecycleInterceptor already guarantees
            // no duplicates via HashSet<PropertyReference?> in _attachedSubjects
            _children.Add(child);
            _childrenCache = default;
        }
    }

    internal void RemoveChild(SubjectPropertyChild child)
    {
        lock (_children)
        {
            var index = -1;
            if (IsSubjectCollection)
            {
                // For collections, match by Subject only — the Index field represents
                // the collection position which shifts as items are removed.
                // Search backwards because LifecycleInterceptor detaches in reverse
                // collection order, making each lookup O(1) instead of O(n).
                var subject = child.Subject;
                for (var i = _children.Count - 1; i >= 0; i--)
                {
                    if (_children[i].Subject == subject)
                    {
                        index = i;
                        break;
                    }
                }
            }
            else
            {
                index = _children.IndexOf(child);
            }

            if (index == -1)
                return;

            _children.RemoveAt(index);
            _childrenCache = default;
        }
    }

    /// <summary>
    /// Syncs children's indices and parent entries with the live collection.
    /// Must be called while LifecycleInterceptor's _attachedSubjects lock is held,
    /// because this method acquires _children then _knownSubjects — the inverse of
    /// HandleLifecycleChange's lock order. The outer _attachedSubjects lock serializes
    /// both paths and prevents deadlock.
    /// </summary>
    /// <param name="collectionValue">The current collection value (passed from caller to avoid re-reading through interceptors).</param>
    /// <param name="registry">The subject registry (passed from caller to avoid repeated service resolution per child).</param>
    internal void RefreshCollectionIndices(object? collectionValue, ISubjectRegistry registry)
    {
        if (!IsSubjectCollection)
            return;

        lock (_children)
        {
            var collectionPositions = BuildCollectionPositions(collectionValue, _children.Count);
            if (collectionPositions is null)
                return;

            for (var i = 0; i < _children.Count; i++)
            {
                var child = _children[i];
                if (!collectionPositions.TryGetValue(child.Subject, out var newIndex))
                    continue;

                // Compare unboxed to avoid allocating a boxed int when index hasn't changed
                if (child.Index is int oldIndex && oldIndex == newIndex)
                    continue;

                var boxedNewIndex = (object)newIndex;
                _children[i] = child with { Index = boxedNewIndex };

                // child is a readonly record struct snapshot from before the update above,
                // so child.Index still holds the old value — correct for the oldIndex parameter.
                registry.TryGetRegisteredSubject(child.Subject)?.UpdateParentIndex(this, child.Index, boxedNewIndex);
            }

            // Sort children to match live collection order
            _children.Sort(static (a, b) => ((int)a.Index!).CompareTo((int)b.Index!));
            _childrenCache = default;

            // Release references so subjects can be GC'd on idle threads
            collectionPositions.Clear();
        }
    }

    /// <summary>
    /// Maps each subject in the collection to its current position.
    /// Uses IList indexed access when available; falls back to ICollection foreach.
    /// Reuses a ThreadStatic dictionary to avoid allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Dictionary<IInterceptorSubject, int>? BuildCollectionPositions(object? value, int capacityHint)
    {
        var collectionPositions = _reusableCollectionPositions;
        collectionPositions?.Clear();

        if (value is IList list)
        {
            for (var index = 0; index < list.Count; index++)
            {
                if (list[index] is IInterceptorSubject subject)
                {
                    collectionPositions ??= _reusableCollectionPositions = new Dictionary<IInterceptorSubject, int>(capacityHint);
                    collectionPositions[subject] = index;
                }
            }
        }
        else if (value is ICollection collection)
        {
            var index = 0;
            foreach (var item in collection)
            {
                if (item is IInterceptorSubject subject)
                {
                    collectionPositions ??= _reusableCollectionPositions = new Dictionary<IInterceptorSubject, int>(capacityHint);
                    collectionPositions[subject] = index;
                }
                index++;
            }
        }

        return collectionPositions;
    }
}
