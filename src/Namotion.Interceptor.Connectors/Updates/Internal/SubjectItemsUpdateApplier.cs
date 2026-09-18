using System.Collections;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Applies collection and dictionary updates from <see cref="SubjectUpdate"/> instances. The items state the
/// complete membership: a listed subject is resolved or created, and a member that is not listed is removed.
/// </summary>
internal static class SubjectItemsUpdateApplier
{
    internal static void ApplyCollectionUpdate(
        PropertyReference property,
        in SubjectPropertyMetadata metadata,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var itemType = metadata.Type.GetCollectionElementType();
        if (metadata.SetValue is null)
        {
            ApplyToHeldMembers(property, propertyUpdate, IndexMembers(metadata.GetValue?.Invoke(property.Subject)),
                keyType: null, itemType, context);
            return;
        }

        if (propertyUpdate.Items is not { } items)
        {
            context.SetPropertyValue(property, propertyUpdate.Timestamp, null);
            return;
        }

        var members = new (IInterceptorSubject Subject, string Id, bool IsCreated)[items.Count];
        var memberCount = 0;
        foreach (var itemUpdate in items)
        {
            if (TryResolveOrCreateMember(itemUpdate.Id, itemType, context, out var member))
            {
                members[memberCount++] = member;
            }
        }

        var subjects = new IInterceptorSubject[memberCount];
        for (var i = 0; i < memberCount; i++)
        {
            subjects[i] = members[i].Subject;
        }

        context.SetPropertyValue(property, propertyUpdate.Timestamp,
            context.SubjectFactory.CreateSubjectCollection(metadata.Type, subjects));

        ApplyMemberPayloads(members, memberCount, context);
    }

    internal static void ApplyDictionaryUpdate(
        PropertyReference property,
        in SubjectPropertyMetadata metadata,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var (keyType, itemType) = metadata.Type.GetDictionaryKeyAndValueTypes();
        if (metadata.SetValue is null)
        {
            var heldValue = metadata.GetValue?.Invoke(property.Subject);
            var heldMembers = heldValue is null ? null : SubjectValueConvert.ToSubjectDictionary(heldValue);
            ApplyToHeldMembers(property, propertyUpdate, heldMembers, keyType, itemType, context);
            return;
        }

        if (propertyUpdate.Items is not { } items)
        {
            context.SetPropertyValue(property, propertyUpdate.Timestamp, null);
            return;
        }

        var entries = new Dictionary<object, IInterceptorSubject>(items.Count);
        var members = new (IInterceptorSubject Subject, string Id, bool IsCreated)[items.Count];
        var memberCount = 0;
        foreach (var itemUpdate in items)
        {
            if (itemUpdate.Key is null)
            {
                // An entry without a key cannot be placed, so it is lost until the next update carrying
                // complete state for this dictionary.
                context.RecordDroppedSubject(itemUpdate.Id);
                continue;
            }

            var key = DictionaryKeyConverter.Convert(itemUpdate.Key, keyType);
            if (entries.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"The update lists the key '{itemUpdate.Key}' of dictionary property '{property.Name}' more than once.");
            }

            if (TryResolveOrCreateMember(itemUpdate.Id, itemType, context, out var member))
            {
                entries.Add(key, member.Subject);
                members[memberCount++] = member;
            }
        }

        context.SetPropertyValue(property, propertyUpdate.Timestamp,
            context.SubjectFactory.CreateSubjectDictionary(metadata.Type, entries));

        ApplyMemberPayloads(members, memberCount, context);
    }

    /// <summary>
    /// Resolves the subject an item names, or creates it populated, without entering it into the graph yet.
    /// Returns <c>false</c> when it is unknown here and the update does not carry its complete state.
    /// </summary>
    private static bool TryResolveOrCreateMember(
        string subjectId,
        Type itemType,
        SubjectUpdateApplyContext context,
        out (IInterceptorSubject Subject, string Id, bool IsCreated) member)
    {
        if (SubjectUpdateApplier.ResolveSubject(subjectId, itemType, context) is { } existingSubject)
        {
            member = (existingSubject, subjectId, false);
            return true;
        }

        var createdSubject = context.TryCreateSubject(subjectId, itemType);
        member = (createdSubject!, subjectId, true);
        return createdSubject is not null;
    }

    /// <summary>
    /// Applies the payloads of the members that already existed, which are only rooted once the container
    /// holding them is written. Created members were populated before.
    /// </summary>
    private static void ApplyMemberPayloads(
        (IInterceptorSubject Subject, string Id, bool IsCreated)[] members,
        int memberCount,
        SubjectUpdateApplyContext context)
    {
        for (var i = 0; i < memberCount; i++)
        {
            if (!members[i].IsCreated)
            {
                context.ApplySubjectPayload(members[i].Subject, members[i].Id);
            }
        }
    }

    /// <summary>
    /// Applies an update to a container this model cannot write: the subjects it holds take the payloads of
    /// the members the update names at their position, adopting their IDs unless an ID names another subject
    /// here. A membership that differs from the held one cannot be stored and is reported as dropped structure.
    /// </summary>
    private static void ApplyToHeldMembers(
        PropertyReference property,
        SubjectPropertyUpdate propertyUpdate,
        IDictionary? heldMembers,
        Type? keyType,
        Type itemType,
        SubjectUpdateApplyContext context)
    {
        if (propertyUpdate.Items is not { } items)
        {
            return;
        }

        var isHeld = (heldMembers?.Count ?? 0) == items.Count;
        for (var i = 0; i < items.Count; i++)
        {
            var itemUpdate = items[i];
            if (keyType is not null && itemUpdate.Key is null)
            {
                // An entry without a key cannot be placed.
                context.RecordDroppedSubject(itemUpdate.Id);
                isHeld = false;
                continue;
            }

            var key = keyType is null ? i : DictionaryKeyConverter.Convert(itemUpdate.Key!, keyType);
            var heldMember = heldMembers?.Contains(key) == true ? heldMembers[key] as IInterceptorSubject : null;

            if (!context.TryResolveSubject(itemUpdate.Id, out var subject) &&
                heldMember is not null &&
                context.TryAdoptSubject(itemUpdate.Id, heldMember))
            {
                subject = heldMember;
            }

            if (subject is null || !ReferenceEquals(subject, heldMember) || !itemType.IsInstanceOfType(subject))
            {
                isHeld = false;
            }

            if (subject is not null)
            {
                context.ApplySubjectPayload(subject, itemUpdate.Id);
            }
        }

        if (!isHeld)
        {
            context.RecordDroppedStructure(property);
        }
    }

    private static IDictionary? IndexMembers(object? collection)
    {
        if (collection is null)
        {
            return null;
        }

        var members = SubjectValueConvert.ToSubjectList(collection);
        var indexedMembers = new Dictionary<object, IInterceptorSubject>(members.Count);
        for (var i = 0; i < members.Count; i++)
        {
            indexedMembers.Add(i, members[i]);
        }

        return indexedMembers;
    }
}
