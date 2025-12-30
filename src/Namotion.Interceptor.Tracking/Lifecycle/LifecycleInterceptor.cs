using System.Collections;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public class LifecycleInterceptor : IWriteInterceptor, ILifecycleInterceptor
{
    private readonly HashSet<IInterceptorSubject> _attachedSubjects = [];

    [ThreadStatic]
    private static Stack<List<(IInterceptorSubject subject, PropertyReference property, object? index)>>? _listPool;

    [ThreadStatic]
    private static Stack<HashSet<IInterceptorSubject>>? _hashSetPool;

    [ThreadStatic]
    private static Stack<List<IInterceptorSubject>>? _subjectListPool;

    /// <summary>
    /// Raised when a subject is attached to the object graph.
    /// Handlers must be exception-free and fast (invoked inside lock).
    /// </summary>
    public event Action<SubjectLifecycleChange>? SubjectAttached;

    /// <summary>
    /// Raised when a subject is detached from the object graph.
    /// Handlers must be exception-free and fast (invoked inside lock).
    /// </summary>
    public event Action<SubjectLifecycleChange>? SubjectDetached;

    public void AttachTo(IInterceptorSubject subject)
    {
        var collectedSubjects = GetList();
        var newlyAttachedSubjects = GetSubjectList();
        try
        {
            lock (_attachedSubjects)
            {
                // Check if root subject is already attached (via property attachment)
                var rootAlreadyAttached = _attachedSubjects.Contains(subject);

                FindSubjectsInProperties(subject, collectedSubjects, null);

                // Attach children first (bottom-up order: children before parent)
                foreach (var child in collectedSubjects)
                {
                    // Skip if child is already attached (was already processed by WriteProperty or earlier)
                    if (!_attachedSubjects.Contains(child.subject))
                    {
                        AttachSubject(child.subject, subject.Context, child.property, child.index, newlyAttachedSubjects);
                    }
                }

                // Then attach root subject (context-only, only if not already attached via property)
                if (!rootAlreadyAttached)
                {
                    AttachSubject(subject, subject.Context, null, null, newlyAttachedSubjects);
                }

                // Phase 2: Call AttachSubjectProperty for all newly attached subjects
                AttachPropertiesForNewlyAttachedSubjects(newlyAttachedSubjects);
            }
        }
        finally
        {
            ReturnList(collectedSubjects);
            ReturnSubjectList(newlyAttachedSubjects);
        }
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
        var collectedSubjects = GetList();
        try
        {
            lock (_attachedSubjects)
            {
                FindSubjectsInProperties(subject, collectedSubjects, null);

                // Detach children first (in reverse order)
                for (var i = collectedSubjects.Count - 1; i >= 0; i--)
                {
                    var child = collectedSubjects[i];
                    DetachSubject(child.subject, subject.Context, child.property, child.index);
                }

                // Then detach root subject (context-only)
                DetachSubject(subject, subject.Context, null, null);
            }
        }
        finally
        {
            ReturnList(collectedSubjects);
        }
    }

    private void AttachSubject(IInterceptorSubject subject, IInterceptorSubjectContext context,
        PropertyReference? property, object? index,
        List<IInterceptorSubject> newlyAttachedSubjects)
    {
        // Check if subject is already attached
        var isFirstAttach = _attachedSubjects.Add(subject);

        // Increment reference count for property attachments
        var referenceCount = property != null
            ? subject.IncrementReferenceCount()
            : subject.GetReferenceCount();

        // Create change for handlers and event
        var change = new SubjectLifecycleChange(
            context,
            subject,
            property,
            index,
            referenceCount);

        // 1. Call attach handlers first (when subject first enters graph)
        if (isFirstAttach)
        {
            foreach (var handler in context.GetServices<ILifecycleHandler>())
            {
                handler.OnSubjectAttached(change);
            }

            if (subject is ILifecycleHandler lifecycleHandler)
            {
                lifecycleHandler.OnSubjectAttached(change);
            }
        }

        // 2. Call reference handlers (when property reference is added)
        if (property != null)
        {
            foreach (var handler in context.GetServices<IReferenceLifecycleHandler>())
            {
                handler.OnSubjectAttachedToProperty(change);
            }

            if (subject is IReferenceLifecycleHandler referenceHandler)
            {
                referenceHandler.OnSubjectAttachedToProperty(change);
            }
        }

        // Fire event only on first attach
        if (isFirstAttach)
        {
            SubjectAttached?.Invoke(change);
            newlyAttachedSubjects.Add(subject);
        }
    }

    private void DetachSubject(IInterceptorSubject subject, IInterceptorSubjectContext context,
        PropertyReference? property, object? index)
    {
        if (!_attachedSubjects.Contains(subject))
        {
            return; // Not attached
        }

        // Decrement reference count for property detachments
        var referenceCount = property != null
            ? subject.DecrementReferenceCount()
            : subject.GetReferenceCount();

        // IsLastDetach is true ONLY for context-only detach (property == null) when ref count is 0
        // Property detach fires first (IsLastDetach=false), then ContextInheritanceHandler triggers
        // context-only detach (IsLastDetach=true)
        var isLastDetach = property == null && referenceCount == 0;
        if (isLastDetach)
        {
            _attachedSubjects.Remove(subject);

            foreach (var propertyName in subject.Properties.Keys)
            {
                subject.DetachSubjectProperty(new PropertyReference(subject, propertyName));
            }
        }

        var change = new SubjectLifecycleChange(
            context,
            subject,
            property,
            index,
            referenceCount);

        // 1. Fire event first on last detach (reverse of attach where event fires last)
        if (isLastDetach)
        {
            SubjectDetached?.Invoke(change);
        }

        // 2. Call reference handlers in REVERSE order (symmetrical to attach)
        if (property != null)
        {
            // TODO: Use reverse for loop for better performance
            foreach (var handler in context.GetServices<IReferenceLifecycleHandler>().Reverse())
            {
                handler.OnSubjectDetachedFromProperty(change);
            }

            if (subject is IReferenceLifecycleHandler referenceHandler)
            {
                referenceHandler.OnSubjectDetachedFromProperty(change);
            }
        }

        // 3. Call detach handlers in REVERSE order (symmetrical to attach)
        if (isLastDetach)
        {
            // TODO: Use reverse for loop for better performance
            foreach (var handler in context.GetServices<ILifecycleHandler>().Reverse())
            {
                handler.OnSubjectDetached(change);
            }

            if (subject is ILifecycleHandler lifecycleHandler)
            {
                lifecycleHandler.OnSubjectDetached(change);
            }

            // Return pooled reference counter after all handlers complete
            subject.ReturnReferenceCounter();
        }
    }

    /// <inheritdoc />
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var currentValue = context.CurrentValue;
        next(ref context);
        var newValue = context.GetFinalValue();

        context.Property.SetWriteTimestamp(SubjectChangeContext.Current.ChangedTimestamp);

        if (typeof(TProperty).IsValueType || typeof(TProperty) == typeof(string))
        {
            return;
        }

        if (currentValue is not (IInterceptorSubject or ICollection or IDictionary) &&
            newValue is not (IInterceptorSubject or ICollection or IDictionary))
        {
            return;
        }

        var oldCollectedSubjects = GetList();
        var newCollectedSubjects = GetList();
        var oldTouchedSubjects = GetHashSet();
        var newTouchedSubjects = GetHashSet();
        var newlyAttachedSubjects = GetSubjectList();

        try
        {
            lock (_attachedSubjects)
            {
                FindSubjectsInProperty(context.Property, currentValue, null, oldCollectedSubjects, oldTouchedSubjects);
                FindSubjectsInProperty(context.Property, newValue, null, newCollectedSubjects, newTouchedSubjects);

                // Detach old subjects (in reverse order)
                for (var i = oldCollectedSubjects.Count - 1; i >= 0; i--)
                {
                    var d = oldCollectedSubjects[i];
                    if (!newTouchedSubjects.Contains(d.subject))
                    {
                        DetachSubject(d.subject, context.Property.Subject.Context, d.property, d.index);
                    }
                }

                // Attach new subjects
                for (var i = 0; i < newCollectedSubjects.Count; i++)
                {
                    var d = newCollectedSubjects[i];
                    if (!oldTouchedSubjects.Contains(d.subject))
                    {
                        AttachSubject(d.subject, context.Property.Subject.Context, d.property, d.index, newlyAttachedSubjects);
                    }
                }

                // Phase 2: Call AttachSubjectProperty for all newly attached subjects
                AttachPropertiesForNewlyAttachedSubjects(newlyAttachedSubjects);
            }
        }
        finally
        {
            ReturnList(oldCollectedSubjects);
            ReturnList(newCollectedSubjects);
            ReturnHashSet(oldTouchedSubjects);
            ReturnHashSet(newTouchedSubjects);
            ReturnSubjectList(newlyAttachedSubjects);
        }
    }

    private void FindSubjectsInProperties(IInterceptorSubject subject,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects,
        HashSet<IInterceptorSubject>? touchedSubjects)
    {
        foreach (var property in subject.Properties)
        {
            var metadata = property.Value;
            if (metadata.IsDerived ||
                metadata.IsIntercepted == false ||
                metadata.Type.IsValueType ||
                metadata.Type == typeof(string))
            {
                continue;
            }

            var propertyValue = metadata.GetValue?.Invoke(subject);
            if (propertyValue is not null)
            {
                FindSubjectsInProperty(new PropertyReference(subject, property.Key), propertyValue, null, collectedSubjects, touchedSubjects);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FindSubjectsInProperty(PropertyReference property,
        object? value, object? index,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> collectedSubjects,
        HashSet<IInterceptorSubject>? touchedSubjects)
    {
        switch (value)
        {
            case IInterceptorSubject subject:
                touchedSubjects?.Add(subject);
                collectedSubjects.Add((subject, property, index));
                break;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject subjectItem)
                    {
                        touchedSubjects?.Add(subjectItem);
                        collectedSubjects.Add((subjectItem, property, entry.Key));
                    }
                }
                break;

            case ICollection collection:
                var i = 0;
                foreach (var item in collection)
                {
                    if (item is IInterceptorSubject subject)
                    {
                        touchedSubjects?.Add(subject);
                        collectedSubjects.Add((subject, property, i));
                    }
                    i++;
                }
                break;
        }
    }

    private static void AttachPropertiesForNewlyAttachedSubjects(List<IInterceptorSubject> newlyAttachedSubjects)
    {
        foreach (var attachedSubject in newlyAttachedSubjects)
        {
            foreach (var propertyName in attachedSubject.Properties.Keys)
            {
                attachedSubject.AttachSubjectProperty(new PropertyReference(attachedSubject, propertyName));
            }
        }
    }

    #region  Performance

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<(IInterceptorSubject subject, PropertyReference property, object? index)> GetList()
    {
        _listPool ??= new Stack<List<(IInterceptorSubject, PropertyReference, object?)>>();
        return _listPool.Count > 0 ? _listPool.Pop() : [];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HashSet<IInterceptorSubject> GetHashSet()
    {
        _hashSetPool ??= new Stack<HashSet<IInterceptorSubject>>();
        return _hashSetPool.Count > 0 ? _hashSetPool.Pop() : [];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnList(List<(IInterceptorSubject, PropertyReference, object?)> list)
    {
        list.Clear();
        _listPool!.Push(list);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnHashSet(HashSet<IInterceptorSubject> hashSet)
    {
        hashSet.Clear();
        _hashSetPool!.Push(hashSet);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<IInterceptorSubject> GetSubjectList()
    {
        _subjectListPool ??= new Stack<List<IInterceptorSubject>>();
        return _subjectListPool.Count > 0 ? _subjectListPool.Pop() : [];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnSubjectList(List<IInterceptorSubject> list)
    {
        list.Clear();
        _subjectListPool!.Push(list);
    }

    #endregion
}
