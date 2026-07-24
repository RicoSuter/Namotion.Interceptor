using System.Globalization;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.LoadPlan;

/// <summary>
/// Discovery planner: browses an OPC UA address space starting from a root node and
/// builds an <see cref="OpcUaLoadPlan"/> that captures all claims, value assignments, and
/// staged-subject links needed for the load. Ownership claims, root assignments, values,
/// subscriptions, and read-after-write registration are all deferred to
/// <see cref="OpcUaLoadPlan.Commit"/>, which applies the plan after discovery succeeds.
/// Two live side effects do occur during discovery, because nested dynamic discovery needs
/// them: dynamic properties are added eagerly to their subjects, and newly created child
/// subjects are linked into the parent context so their own children are discoverable.
/// On any planning failure the planner rolls back all staged-subject links it established
/// (deepest-first, mirroring the order they were added), so no subjects leak into the
/// registry on error; a leftover eager dynamic property is transient and is re-matched by
/// the next load through its attached node-id attribute.
/// </summary>
internal sealed class OpcUaLoadPlanner
{
    private readonly IInterceptorSubject _rootSubject;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectClientSource _source;
    private readonly ILogger _logger;

    // Browse cache shared across all phases including attribute traversal.
    private readonly Dictionary<NodeId, IReadOnlyList<ReferenceDescription>> _browseCache = new();

    // Discovery-time staged-subject links, maintained for rollback on planning failure.
    // The order matches AddFallbackContext call order so rollback runs deepest-first.
    private readonly List<(IInterceptorSubject Subject, IInterceptorSubjectContext ParentContext)> _discoveryLinks = new();

    // Discovery-only reuse maps (pure data; never mutate the live graph).
    private readonly Dictionary<NodeId, IInterceptorSubject> _subjectsByNodeId = new();
    private readonly HashSet<IInterceptorSubject> _loadedSubjects = new();

    public OpcUaLoadPlanner(
        IInterceptorSubject rootSubject,
        OpcUaClientConfiguration configuration,
        OpcUaSubjectClientSource source,
        ILogger logger)
    {
        _rootSubject = rootSubject;
        _configuration = configuration;
        _source = source;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full multi-phase discovery and returns a populated plan. Rolls back all
    /// staged-subject links and rethrows on any exception.
    /// </summary>
    public async Task<OpcUaLoadPlan> CreatePlanAsync(
        IInterceptorSubject subject,
        ReferenceDescription rootNode,
        ISession session,
        CancellationToken cancellationToken)
    {
        var plan = new OpcUaLoadPlan(_rootSubject, _logger);
        try
        {
            await LoadSubjectsAsync([(rootNode, subject)], plan, session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Rollback discovery-time AddFallbackContext calls deepest-first. One failed
            // detach must not strand the rest or mask the original exception.
            for (var i = _discoveryLinks.Count - 1; i >= 0; i--)
            {
                var (staged, parentContext) = _discoveryLinks[i];
                try
                {
                    staged.Context.RemoveFallbackContext(parentContext);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to detach staged subject {Subject} from parent context during discovery rollback.",
                        staged.GetType().Name);
                }
            }
            _discoveryLinks.Clear();
            throw;
        }
        return plan;
    }

    // -----------------------------------------------------------------------------------------
    // Core multi-phase discovery (mirrors PR 313's LoadSubjectsAsync phases)
    // -----------------------------------------------------------------------------------------

    private async Task LoadSubjectsAsync(
        IReadOnlyList<(ReferenceDescription Node, IInterceptorSubject Subject)> subjects,
        OpcUaLoadPlan plan,
        ISession session,
        CancellationToken cancellationToken)
    {
        if (subjects.Count == 0)
        {
            return;
        }

        // Phase 1: filter and batch-browse
        var (validSubjects, allBrowseResults) = await FilterAndBrowseSubjectsAsync(subjects, session, cancellationToken).ConfigureAwait(false);
        if (validSubjects.Count == 0)
        {
            return;
        }

        // Phase 2: classify children per subject, collect dynamic nodes
        var allDynamicObjectNodeIds = new HashSet<NodeId>();
        var allDynamicVariableNodes = new Dictionary<NodeId, ReferenceDescription>();
        var subjectStates = new List<SubjectState>(validSubjects.Count);

        foreach (var (subject, registeredSubject, subjectNodeId) in validSubjects)
        {
            var browseResults = subjectNodeId is not null &&
                allBrowseResults.TryGetValue(subjectNodeId, out var collection) ? collection : [];

            var distinctReferences = session.DistinctByResolvedNodeId(browseResults, _logger);

            var childEntries = await ClassifyChildReferencesAsync(
                registeredSubject, distinctReferences,
                allDynamicObjectNodeIds, allDynamicVariableNodes,
                session, cancellationToken).ConfigureAwait(false);

            subjectStates.Add(new SubjectState(subject, registeredSubject, childEntries));
        }

        // Phase 3: batch-resolve types for dynamic nodes
        var objectTypeMap = await ResolveObjectTypesAsync(allDynamicObjectNodeIds, session, cancellationToken).ConfigureAwait(false);

        var variableTypeMap = allDynamicVariableNodes.Count > 0
            ? await _configuration.TypeResolver!.ResolveVariableTypesAsync(session, allDynamicVariableNodes.Values, cancellationToken).ConfigureAwait(false)
            : new Dictionary<NodeId, Type?>();

        // Phase 4: dispatch properties; attribute discovery runs at the end
        await LoadChildPropertiesAsync(subjectStates, objectTypeMap, variableTypeMap, plan, session, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(
        List<(IInterceptorSubject Subject, RegisteredSubject RegisteredSubject, NodeId? NodeId)> ValidSubjects,
        Dictionary<NodeId, IReadOnlyList<ReferenceDescription>> BrowseResults)>
        FilterAndBrowseSubjectsAsync(
            IReadOnlyList<(ReferenceDescription Node, IInterceptorSubject Subject)> subjects,
            ISession session,
            CancellationToken cancellationToken)
    {
        var validSubjects = new List<(IInterceptorSubject, RegisteredSubject, NodeId?)>(subjects.Count);
        var subjectNodeIds = new List<NodeId>(subjects.Count);

        foreach (var (node, subject) in subjects)
        {
            if (!_loadedSubjects.Add(subject))
            {
                continue;
            }

            var registeredSubject = subject.TryGetRegisteredSubject();
            if (registeredSubject is null)
            {
                continue;
            }

            var resolved = ExpandedNodeId.ToNodeId(node.NodeId, session.NamespaceUris);
            if (resolved is null)
            {
                _logger.LogWarning(
                    "Could not resolve NodeId '{NodeId}' for subject '{Subject}': the namespace URI is not registered in the session's NamespaceTable. The subject will be loaded with no children.",
                    node.NodeId, subject.GetType().Name);
            }
            validSubjects.Add((subject, registeredSubject, resolved));
            if (resolved is not null)
            {
                subjectNodeIds.Add(resolved);
            }
        }

        var browseResults = await BrowseAsync(subjectNodeIds, session, cancellationToken).ConfigureAwait(false);
        return (validSubjects, browseResults);
    }

    private async Task<List<ChildEntry>> ClassifyChildReferencesAsync(
        RegisteredSubject registeredSubject,
        List<(ReferenceDescription Reference, NodeId NodeId)> distinctReferences,
        HashSet<NodeId> dynamicObjectNodeIds,
        Dictionary<NodeId, ReferenceDescription> dynamicVariableNodes,
        ISession session,
        CancellationToken cancellationToken)
    {
        var childEntries = new List<ChildEntry>(distinctReferences.Count);
        var stagedDynamicNames = new HashSet<string>();

        for (var i = 0; i < distinctReferences.Count; i++)
        {
            var (nodeReference, resolvedNodeId) = distinctReferences[i];

            var property = await _configuration.Mapper
                .TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, session, _rootSubject), registeredSubject, cancellationToken)
                .ConfigureAwait(false);

            if (property is not null)
            {
                childEntries.Add(new ChildEntry(nodeReference, resolvedNodeId, property));
                continue;
            }

            if (registeredSubject.TryGetProperty(nodeReference.BrowseName.Name) is not null)
            {
                _logger.LogDebug(
                    "Skipping OPC UA child '{BrowseName}' (NodeId: {NodeId}): a property with this name is already declared on the registered subject but the mapper returned no mapping (likely a BrowseName collision with an unmapped property, or a mapping that targets a different NodeId).",
                    nodeReference.BrowseName.Name, nodeReference.NodeId);
                continue;
            }

            var addAsDynamic = _configuration.ShouldAddDynamicProperty is not null &&
                await _configuration.ShouldAddDynamicProperty(nodeReference, cancellationToken).ConfigureAwait(false);

            if (!addAsDynamic)
            {
                continue;
            }

            // Two siblings with the same BrowseName but different NodeIds survive
            // DistinctByResolvedNodeId and the TryGetProperty check above. Skip the
            // second so AddProperty is never called twice with the same name.
            if (!stagedDynamicNames.Add(nodeReference.BrowseName.Name))
            {
                _logger.LogWarning(
                    "Skipping OPC UA child '{BrowseName}' (NodeId: {NodeId}): a sibling reference already staged a dynamic property with this BrowseName under the same parent.",
                    nodeReference.BrowseName.Name, nodeReference.NodeId);
                continue;
            }

            childEntries.Add(new ChildEntry(nodeReference, resolvedNodeId, null));
            if (nodeReference.NodeClass == NodeClass.Object)
            {
                dynamicObjectNodeIds.Add(resolvedNodeId);
            }
            else if (nodeReference.NodeClass == NodeClass.Variable)
            {
                dynamicVariableNodes.TryAdd(resolvedNodeId, nodeReference);
            }
        }

        return childEntries;
    }

    private async Task<Dictionary<NodeId, Type>> ResolveObjectTypesAsync(
        IReadOnlyCollection<NodeId> objectNodeIds,
        ISession session,
        CancellationToken cancellationToken)
    {
        var objectTypeMap = new Dictionary<NodeId, Type>();
        if (objectNodeIds.Count == 0)
        {
            return objectTypeMap;
        }

        var objectBrowseResults = await BrowseAsync(objectNodeIds, session, cancellationToken).ConfigureAwait(false);
        foreach (var nodeId in objectNodeIds)
        {
            if (objectBrowseResults.TryGetValue(nodeId, out var children))
            {
                objectTypeMap[nodeId] = _configuration.TypeResolver!.ResolveObjectNodeType(children);
            }
        }

        return objectTypeMap;
    }

    private async Task LoadChildPropertiesAsync(
        List<SubjectState> subjectStates,
        Dictionary<NodeId, Type> objectTypeMap,
        IReadOnlyDictionary<NodeId, Type?> variableTypeMap,
        OpcUaLoadPlan plan,
        ISession session,
        CancellationToken cancellationToken)
    {
        var allAttributeVariableNodes = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var allPendingSubjectRefs = new List<PendingSubjectRef>();
        var pendingVariableSubjects = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var pendingCollections = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var pendingDictionaries = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();

        // Two sibling references with different NodeIds can map to the same property.
        // For subject, collection, and dictionary properties the assignments are deferred,
        // so the duplicate would create and stage a second subject tree that the final
        // assignment then overwrites, leaving committed orphans. Dedupe by destination
        // property; the first reference wins (browse order).
        var structuredPropertyTargets = new HashSet<RegisteredSubjectProperty>();

        bool TryClaimStructuredTarget(RegisteredSubjectProperty targetProperty, ReferenceDescription reference, NodeId nodeId)
        {
            if (structuredPropertyTargets.Add(targetProperty))
            {
                return true;
            }

            _logger.LogWarning(
                "Skipping OPC UA child '{BrowseName}' (NodeId: {NodeId}): another sibling reference already targets property '{Subject}.{Property}' in this load; the first reference wins.",
                reference.BrowseName.Name, nodeId, targetProperty.Subject.GetType().Name, targetProperty.Name);
            return false;
        }

        foreach (var (stateSubject, stateRegisteredSubject, stateChildEntries) in subjectStates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < stateChildEntries.Count; i++)
            {
                var (nodeReference, resolvedNodeId, property) = stateChildEntries[i];

                property ??= TryCreateDynamicProperty(
                    stateRegisteredSubject, nodeReference, resolvedNodeId,
                    objectTypeMap, variableTypeMap, session);

                if (property is null)
                {
                    continue;
                }

                if (!_configuration.Mapper.TryGetMapping(property, _rootSubject, out var nodeConfiguration))
                {
                    continue;
                }

                if (property.IsSubjectReference)
                {
                    if (nodeConfiguration.NodeClass == OpcUaNodeClass.Variable)
                    {
                        pendingVariableSubjects.Add((property, resolvedNodeId));
                    }
                    else
                    {
                        if (!TryClaimStructuredTarget(property, nodeReference, resolvedNodeId))
                        {
                            continue;
                        }

                        var result = await PrepareSubjectReferenceAsync(
                            property, nodeReference, resolvedNodeId, stateSubject,
                            plan, session, cancellationToken).ConfigureAwait(false);

                        if (result is not null)
                        {
                            var (subjectToLoad, isNew) = result.Value;
                            allPendingSubjectRefs.Add(new PendingSubjectRef(property, nodeReference, subjectToLoad, isNew));
                        }
                    }
                }
                else if (property.IsSubjectCollection)
                {
                    if (!TryClaimStructuredTarget(property, nodeReference, resolvedNodeId))
                    {
                        continue;
                    }

                    pendingCollections.Add((property, resolvedNodeId));
                }
                else if (property.IsSubjectDictionary)
                {
                    if (!TryClaimStructuredTarget(property, nodeReference, resolvedNodeId))
                    {
                        continue;
                    }

                    pendingDictionaries.Add((property, resolvedNodeId));
                }
                else
                {
                    MonitorValueNode(plan, property, resolvedNodeId);
                    allAttributeVariableNodes.Add((property, resolvedNodeId));
                }
            }
        }

        await BatchLoadVariableNodesAsync(pendingVariableSubjects, plan, session, cancellationToken).ConfigureAwait(false);
        await BatchLoadCollectionsAndDictionariesAsync(pendingCollections, pendingDictionaries, plan, session, cancellationToken).ConfigureAwait(false);
        await LoadPendingSubjectReferencesAsync(allPendingSubjectRefs, plan, session, cancellationToken).ConfigureAwait(false);
        await LoadAttributesAsync(allAttributeVariableNodes, plan, session, cancellationToken).ConfigureAwait(false);
    }

    private RegisteredSubjectProperty? TryCreateDynamicProperty(
        RegisteredSubject registeredSubject,
        ReferenceDescription nodeReference,
        NodeId resolvedNodeId,
        Dictionary<NodeId, Type> objectTypeMap,
        IReadOnlyDictionary<NodeId, Type?> variableTypeMap,
        ISession session)
    {
        Type? inferredType = null;
        if (nodeReference.NodeClass == NodeClass.Object)
        {
            objectTypeMap.TryGetValue(resolvedNodeId, out inferredType);
        }
        else if (nodeReference.NodeClass == NodeClass.Variable)
        {
            variableTypeMap.TryGetValue(resolvedNodeId, out inferredType);
        }

        if (inferredType is null)
        {
            _logger.LogWarning(
                "Could not infer type for dynamic property '{PropertyName}' (NodeId: {NodeId}). Skipping property.",
                nodeReference.BrowseName.Name, nodeReference.NodeId);
            return null;
        }

        object? value = null;
        // Added eagerly on non-staged subjects: leftover from a failed load is transient
        // because the next load re-matches via the attached OpcUaNodeAttribute.
        return registeredSubject.AddProperty(
            nodeReference.BrowseName.Name,
            inferredType,
            _ => value,
            (_, o) => value = o,
            _configuration.TypeResolver!.GetDynamicPropertyAttributes(nodeReference, session));
    }

    private async Task<(IInterceptorSubject Subject, bool IsNew)?> PrepareSubjectReferenceAsync(
        RegisteredSubjectProperty property,
        ReferenceDescription nodeReference,
        NodeId resolvedNodeId,
        IInterceptorSubject parentSubject,
        OpcUaLoadPlan plan,
        ISession session,
        CancellationToken cancellationToken)
    {
        if (_subjectsByNodeId.TryGetValue(resolvedNodeId, out var reusedSubject))
        {
            plan.AddValueAssignment(_source, property, reusedSubject);
            return null;
        }

        var existingChildren = property.Children;
        var existingSubject = existingChildren.IsEmpty ? null : existingChildren[0].Subject;
        var subjectToLoad = existingSubject
            ?? await _configuration.SubjectFactory.CreateSubjectAsync(property, nodeReference, session, cancellationToken).ConfigureAwait(false);

        if (existingSubject is null)
        {
            RegisterStagedSubject(subjectToLoad, parentSubject.Context, plan);
        }

        _subjectsByNodeId.TryAdd(resolvedNodeId, subjectToLoad);
        return (subjectToLoad, existingSubject is null);
    }

    // Variable subjects' value properties intentionally don't enter LoadAttributesAsync.
    // OPC UA Variable attributes should be modeled as peer properties of the Variable
    // subject; C# .Attributes on the Value property are not discovered here by design.
    private async Task BatchLoadVariableNodesAsync(
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> variableNodes,
        OpcUaLoadPlan plan,
        ISession session,
        CancellationToken cancellationToken)
    {
        if (variableNodes.Count == 0)
        {
            return;
        }

        // Dedup by childSubject: when two parent properties reference the same Variable
        // subject (graph-shaped address space), pick the smaller NodeId for a reproducible
        // outcome regardless of browse order, matching AddClaim's tie-break.
        var nodeByChildSubject = new Dictionary<RegisteredSubject, NodeId>();

        foreach (var (property, nodeId) in variableNodes)
        {
            var children = property.Children;
            var childSubject = (children.IsEmpty ? null : (IInterceptorSubject?)children[0].Subject)?.TryGetRegisteredSubject();
            if (childSubject is null)
            {
                _logger.LogWarning(
                    "Skipping OPC UA Variable-typed subject reference '{Subject}.{Property}' (NodeId: {NodeId}): no child subject was pre-constructed. The parent type must instantiate this property in its constructor.",
                    property.Subject.GetType().Name, property.Name, nodeId);
                continue;
            }

            if (!nodeByChildSubject.TryGetValue(childSubject, out var existing) || nodeId.CompareTo(existing) < 0)
            {
                nodeByChildSubject[childSubject] = nodeId;
            }
        }

        if (nodeByChildSubject.Count == 0)
        {
            return;
        }

        var nodesToBrowse = new List<(NodeId NodeId, RegisteredSubject ChildSubject, RegisteredSubjectProperty? ValueProperty)>(nodeByChildSubject.Count);
        var nodeIds = new HashSet<NodeId>(nodeByChildSubject.Count);
        foreach (var (childSubject, nodeId) in nodeByChildSubject)
        {
            var valueProperty = childSubject.TryGetValueProperty(_configuration.Mapper, _rootSubject)?.Property;
            if (valueProperty is not null)
            {
                MonitorValueNode(plan, valueProperty, nodeId);
            }
            nodesToBrowse.Add((nodeId, childSubject, valueProperty));
            nodeIds.Add(nodeId);
        }

        var browseResults = await BrowseAsync(nodeIds, session, cancellationToken).ConfigureAwait(false);

        foreach (var (nodeId, childSubject, valueProperty) in nodesToBrowse)
        {
            if (!browseResults.TryGetValue(nodeId, out var rawChildren))
            {
                continue;
            }

            var childNodes = session.DistinctByResolvedNodeId(rawChildren, _logger);

            var propertiesByName = new Dictionary<string, RegisteredSubjectProperty>(childSubject.Properties.Length);
            foreach (var childProperty in childSubject.Properties)
            {
                if (childProperty == valueProperty)
                {
                    continue;
                }
                var name = childProperty.ResolvePropertyName(_configuration.Mapper, _rootSubject);
                if (name is null)
                {
                    continue;
                }
                if (!propertiesByName.TryAdd(name, childProperty))
                {
                    _logger.LogWarning(
                        "Variable subject '{Subject}' has multiple properties resolving to the OPC UA browse name '{BrowseName}'. Keeping '{KeptProperty}', dropping '{DroppedProperty}'. Check the Mapper for browse-name collisions.",
                        childSubject.Subject.GetType().Name, name, propertiesByName[name].Name, childProperty.Name);
                }
            }

            foreach (var (childNode, childNodeId) in childNodes)
            {
                if (propertiesByName.TryGetValue(childNode.BrowseName.Name, out var match))
                {
                    MonitorValueNode(plan, match, childNodeId);
                }
            }
        }
    }

    private async Task BatchLoadCollectionsAndDictionariesAsync(
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> pendingCollections,
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> pendingDictionaries,
        OpcUaLoadPlan plan,
        ISession session,
        CancellationToken cancellationToken)
    {
        if (pendingCollections.Count + pendingDictionaries.Count == 0)
        {
            return;
        }

        var allNodeIds = new HashSet<NodeId>(pendingCollections.Count + pendingDictionaries.Count);
        foreach (var (_, nodeId) in pendingCollections) allNodeIds.Add(nodeId);
        foreach (var (_, nodeId) in pendingDictionaries) allNodeIds.Add(nodeId);

        var browseResults = await BrowseAsync(allNodeIds, session, cancellationToken).ConfigureAwait(false);
        var allChildrenToLoad = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>();

        foreach (var (property, nodeId) in pendingCollections)
        {
            if (!browseResults.TryGetValue(nodeId, out var rawChildren))
            {
                // Missing = browse failed permanently; keep existing items rather than
                // overwriting with empty collection.
                _logger.LogWarning(
                    "Skipping OPC UA collection '{Subject}.{Property}' (NodeId: {NodeId}): browse failed this load; existing items are preserved.",
                    property.Subject.GetType().Name, property.Name, nodeId);
                continue;
            }

            var childNodes = session.DistinctByResolvedNodeId(rawChildren, _logger);

            var children = await ResolveChildSubjectsAsync(property, childNodes, isDictionary: false, plan, session, cancellationToken).ConfigureAwait(false);

            var collection = DefaultSubjectFactory.Instance.CreateSubjectCollection(property.Type, children.Select(c => c.Subject));
            plan.AddValueAssignment(_source, property, collection);
            allChildrenToLoad.AddRange(children);
        }

        foreach (var (property, nodeId) in pendingDictionaries)
        {
            if (!browseResults.TryGetValue(nodeId, out var rawChildren))
            {
                _logger.LogWarning(
                    "Skipping OPC UA dictionary '{Subject}.{Property}' (NodeId: {NodeId}): browse failed this load; existing entries are preserved.",
                    property.Subject.GetType().Name, property.Name, nodeId);
                continue;
            }

            var childNodes = session.DistinctByResolvedNodeId(rawChildren, _logger);

            // Two children can extract to the same dictionary key. Dedupe before creating
            // subjects so the loser is never staged or monitored.
            var seenKeys = new HashSet<string>(childNodes.Count);
            var dedupedChildNodes = new List<(ReferenceDescription Reference, NodeId NodeId)>(childNodes.Count);
            foreach (var childNode in childNodes)
            {
                if (seenKeys.Add(ExtractDictionaryKey(childNode.Reference.BrowseName.Name)))
                {
                    dedupedChildNodes.Add(childNode);
                }
                else
                {
                    _logger.LogWarning(
                        "Skipping OPC UA dictionary child '{BrowseName}' (NodeId: {NodeId}): a sibling already produced the same dictionary key.",
                        childNode.Reference.BrowseName.Name, childNode.NodeId);
                }
            }

            var children = await ResolveChildSubjectsAsync(property, dedupedChildNodes, isDictionary: true, plan, session, cancellationToken).ConfigureAwait(false);

            var entries = new Dictionary<object, IInterceptorSubject>(children.Count);
            foreach (var (node, subject) in children)
            {
                entries[ExtractDictionaryKey(node.BrowseName.Name)] = subject;
            }

            var dictionary = DefaultSubjectFactory.Instance.CreateSubjectDictionary(property.Type, entries);
            plan.AddValueAssignment(_source, property, dictionary);
            allChildrenToLoad.AddRange(children);
        }

        await LoadSubjectsAsync(allChildrenToLoad, plan, session, cancellationToken).ConfigureAwait(false);
    }

    // Reuses existing children when possible: dictionaries by browse name, collections by index.
    private async Task<List<(ReferenceDescription Node, IInterceptorSubject Subject)>> ResolveChildSubjectsAsync(
        RegisteredSubjectProperty property,
        List<(ReferenceDescription Reference, NodeId NodeId)> childNodes,
        bool isDictionary,
        OpcUaLoadPlan plan,
        ISession session,
        CancellationToken cancellationToken)
    {
        var existingChildren = property.Children;
        var existingByKey = new Dictionary<string, IInterceptorSubject?>(isDictionary ? existingChildren.Length : 0);
        if (isDictionary)
        {
            foreach (var existing in existingChildren)
            {
                if (existing.Index is { } index &&
                    Convert.ToString(index, CultureInfo.InvariantCulture) is { } existingKey)
                {
                    existingByKey[existingKey] = existing.Subject;
                }
            }
        }

        var children = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(childNodes.Count);

        for (var i = 0; i < childNodes.Count; i++)
        {
            var (childNode, nodeId) = childNodes[i];

            IInterceptorSubject? childSubject;
            object factoryIndex;

            if (isDictionary)
            {
                var key = ExtractDictionaryKey(childNode.BrowseName.Name);
                existingByKey.TryGetValue(key, out childSubject);
                factoryIndex = key;
            }
            else
            {
                childSubject = i < existingChildren.Length ? existingChildren[i].Subject : null;
                factoryIndex = i;
            }

            if (childSubject is null)
            {
                _subjectsByNodeId.TryGetValue(nodeId, out childSubject);
            }

            var isFreshlyCreated = childSubject is null;
            childSubject ??= await _configuration.SubjectFactory.CreateCollectionSubjectAsync(
                property, childNode, factoryIndex, session, cancellationToken).ConfigureAwait(false);

            if (isFreshlyCreated)
            {
                RegisterStagedSubject(childSubject, property.Subject.Context, plan);
            }

            _subjectsByNodeId.TryAdd(nodeId, childSubject);
            children.Add((childNode, childSubject));
        }

        return children;
    }

    private async Task LoadPendingSubjectReferencesAsync(
        List<PendingSubjectRef> pendingRefs,
        OpcUaLoadPlan plan,
        ISession session,
        CancellationToken cancellationToken)
    {
        if (pendingRefs.Count == 0)
        {
            return;
        }

        var refsToLoad = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(pendingRefs.Count);
        foreach (var pending in pendingRefs)
        {
            refsToLoad.Add((pending.NodeReference, pending.SubjectToLoad));
        }
        await LoadSubjectsAsync(refsToLoad, plan, session, cancellationToken).ConfigureAwait(false);

        foreach (var pending in pendingRefs)
        {
            if (pending.IsNew)
            {
                plan.AddValueAssignment(_source, pending.Property, pending.SubjectToLoad);
            }
        }
    }

    // -----------------------------------------------------------------------------------------
    // Attribute discovery (ported from PR 313's OpcUaAttributeLoader)
    // -----------------------------------------------------------------------------------------

    private async Task LoadAttributesAsync(
        IReadOnlyList<(RegisteredSubjectProperty Property, NodeId NodeId)> variableNodes,
        OpcUaLoadPlan plan,
        ISession session,
        CancellationToken cancellationToken)
    {
        if (variableNodes.Count == 0)
        {
            return;
        }

        // Tracks (property, parentNodeId) pairs already processed in any prior round to
        // avoid re-claiming already-monitored sub-attribute references.
        var processedEntries = new HashSet<(RegisteredSubjectProperty Property, NodeId ParentNodeId)>();
        var currentRound = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>(variableNodes);

        var traversal = 0;
        while (currentRound.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++traversal > _configuration.MaxAttributeTraversals)
            {
                _logger.LogWarning(
                    "Aborting attribute traversal after {MaxTraversals} levels with {Remaining} entries still pending. Possible cycle in address space or attribute registration.",
                    _configuration.MaxAttributeTraversals, currentRound.Count);
                break;
            }

            currentRound = await ProcessAttributeRoundAsync(currentRound, processedEntries, plan, session, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<(RegisteredSubjectProperty Property, NodeId NodeId)>> ProcessAttributeRoundAsync(
        List<(RegisteredSubjectProperty Property, NodeId ParentNodeId)> currentRound,
        HashSet<(RegisteredSubjectProperty Property, NodeId ParentNodeId)> processedEntries,
        OpcUaLoadPlan plan,
        ISession session,
        CancellationToken cancellationToken)
    {
        var parentIds = new HashSet<NodeId>(currentRound.Count);
        foreach (var (_, parentNodeId) in currentRound)
        {
            parentIds.Add(parentNodeId);
        }

        var browseResults = await BrowseAsync(parentIds, session, cancellationToken).ConfigureAwait(false);

        // Memoize per parent so two entries sharing a parent don't recompute.
        var distinctChildren = new Dictionary<NodeId, List<(ReferenceDescription Reference, NodeId NodeId)>>(browseResults.Count);
        foreach (var (parentNodeId, rawChildren) in browseResults)
        {
            distinctChildren[parentNodeId] = session.DistinctByResolvedNodeId(rawChildren, _logger);
        }

        var dynamicAttributeNodes = new List<DynamicAttributeNode>();
        var nextRound = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();

        foreach (var (property, parentNodeId) in currentRound)
        {
            if (!processedEntries.Add((property, parentNodeId)))
            {
                continue;
            }

            if (!distinctChildren.TryGetValue(parentNodeId, out var childNodes) || childNodes.Count == 0)
            {
                continue;
            }

            var processedBrowseNames = MatchKnownAttributes(property, childNodes, nextRound, plan, session, cancellationToken);
            await CollectDynamicAttributesAsync(property, parentNodeId, childNodes, processedBrowseNames, dynamicAttributeNodes, cancellationToken).ConfigureAwait(false);
        }

        await CreateDynamicAttributesAsync(dynamicAttributeNodes, nextRound, plan, session, cancellationToken).ConfigureAwait(false);
        return nextRound;
    }

    private HashSet<string> MatchKnownAttributes(
        RegisteredSubjectProperty property,
        List<(ReferenceDescription Reference, NodeId NodeId)> childNodes,
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> nextRound,
        OpcUaLoadPlan plan,
        ISession session,
        CancellationToken cancellationToken)
    {
        var childByBrowseName = new Dictionary<string, NodeId>(childNodes.Count);
        foreach (var (childNode, childNodeId) in childNodes)
        {
            childByBrowseName.TryAdd(childNode.BrowseName.Name, childNodeId);
        }

        var processedBrowseNames = new HashSet<string>();
        foreach (var attribute in property.Attributes)
        {
            if (!_configuration.Mapper.TryGetMapping(attribute, _rootSubject, out var attributeConfiguration))
            {
                continue;
            }

            var attributeBrowseName = attributeConfiguration.BrowseName ?? attribute.BrowseName;
            if (!childByBrowseName.TryGetValue(attributeBrowseName, out var matchingNodeId))
            {
                continue;
            }

            processedBrowseNames.Add(attributeBrowseName);
            MonitorValueNode(plan, attribute, matchingNodeId);
            nextRound.Add((attribute, matchingNodeId));
        }
        return processedBrowseNames;
    }

    private async Task CollectDynamicAttributesAsync(
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        List<(ReferenceDescription Reference, NodeId NodeId)> childNodes,
        HashSet<string> processedBrowseNames,
        List<DynamicAttributeNode> dynamicAttributeNodes,
        CancellationToken cancellationToken)
    {
        foreach (var (childNode, childNodeId) in childNodes)
        {
            if (childNode.NodeClass != NodeClass.Variable)
            {
                continue;
            }

            var browseName = childNode.BrowseName.Name;
            if (!processedBrowseNames.Add(browseName))
            {
                continue;
            }

            if (property.TryGetAttribute(browseName) is not null)
            {
                _logger.LogWarning(
                    "Skipping OPC UA child '{AttributeName}' on '{PropertyName}' (parent {ParentNodeId}, child {ChildNodeId}): an attribute with this name is already declared on the property (a C# attribute without an OPC UA mapping, or registered by a lifecycle handler).",
                    browseName, property.Name, parentNodeId, childNode.NodeId);
                continue;
            }

            var addAsDynamic = _configuration.ShouldAddDynamicAttribute is not null &&
                await _configuration.ShouldAddDynamicAttribute(childNode, cancellationToken).ConfigureAwait(false);
            if (!addAsDynamic)
            {
                continue;
            }

            dynamicAttributeNodes.Add(new DynamicAttributeNode(property, childNodeId, childNode, browseName));
        }
    }

    private async Task CreateDynamicAttributesAsync(
        List<DynamicAttributeNode> dynamicAttributeNodes,
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> nextRound,
        OpcUaLoadPlan plan,
        ISession session,
        CancellationToken cancellationToken)
    {
        if (dynamicAttributeNodes.Count == 0)
        {
            return;
        }

        var variableReferences = new List<ReferenceDescription>(dynamicAttributeNodes.Count);
        foreach (var entry in dynamicAttributeNodes)
        {
            variableReferences.Add(entry.ChildNode);
        }
        var resolvedTypes = await _configuration.TypeResolver!.ResolveVariableTypesAsync(session, variableReferences, cancellationToken).ConfigureAwait(false);

        foreach (var entry in dynamicAttributeNodes)
        {
            resolvedTypes.TryGetValue(entry.ChildNodeId, out var inferredType);
            if (inferredType is null)
            {
                _logger.LogWarning(
                    "Could not infer type for dynamic attribute '{AttributeName}' on property '{PropertyName}' (NodeId: {NodeId}). Skipping attribute.",
                    entry.BrowseName, entry.OwnerProperty.Name, entry.ChildNode.NodeId);
                continue;
            }

            // Two entries in the same round can stage the same (property, browse name):
            // re-check so the duplicate is skipped instead of crashing AddAttribute.
            if (entry.OwnerProperty.TryGetAttribute(entry.BrowseName) is not null)
            {
                _logger.LogWarning(
                    "Skipping dynamic attribute '{AttributeName}' on property '{PropertyName}' (NodeId: {NodeId}): an attribute with this name was already created by a sibling entry in this round.",
                    entry.BrowseName, entry.OwnerProperty.Name, entry.ChildNode.NodeId);
                continue;
            }

            object? value = null;
            // Added eagerly like TryCreateDynamicProperty: leftover from a failed load
            // is transient; MatchKnownAttributes re-matches via the attached OpcUaNodeAttribute
            // on the next load and re-monitors it.
            var dynamicAttribute = entry.OwnerProperty.AddAttribute(
                entry.BrowseName,
                inferredType,
                _ => value,
                (_, o) => value = o,
                _configuration.TypeResolver!.GetDynamicPropertyAttributes(entry.ChildNode, session));

            MonitorValueNode(plan, dynamicAttribute, entry.ChildNodeId);
            nextRound.Add((dynamicAttribute, entry.ChildNodeId));
        }
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Pre-checks foreign ownership, creates a monitored item, and queues a claim on the plan.
    /// </summary>
    private void MonitorValueNode(OpcUaLoadPlan plan, RegisteredSubjectProperty property, NodeId nodeId)
    {
        // Pre-check skips MonitoredItem creation for properties already owned by another source.
        if (property.Reference.TryGetSource(out var existing) && !ReferenceEquals(existing, _source))
        {
            _logger.LogError(
                "Property {Subject}.{Property} already owned by another source. Skipping OPC UA monitoring.",
                property.Subject.GetType().Name, property.Name);
            return;
        }

        var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property, _rootSubject);
        plan.AddClaim(property.Reference, nodeId, monitoredItem);
    }

    /// <summary>
    /// Links a newly-created staged subject into the parent context during discovery
    /// (so its children are discoverable via TryGetRegisteredSubject) and records the
    /// link in the planner's rollback list and the plan's staged-subject list.
    /// </summary>
    private void RegisterStagedSubject(
        IInterceptorSubject subject,
        IInterceptorSubjectContext parentContext,
        OpcUaLoadPlan plan)
    {
        // Record the rollback entry BEFORE the side effect so an exception in
        // AddFallbackContext doesn't leave a link that cannot be rolled back.
        _discoveryLinks.Add((subject, parentContext));
        try
        {
            subject.Context.AddFallbackContext(parentContext);
        }
        catch
        {
            _discoveryLinks.RemoveAt(_discoveryLinks.Count - 1);
            throw;
        }

        // Also tell the plan, so Commit's step 1 re-linking is a harmless dedup.
        plan.AddStagedSubject(subject, parentContext);
    }

    /// <summary>
    /// Batch-browses the given node IDs, serving cached entries from the planner-level
    /// browse cache and issuing a single session call for any cache misses.
    /// A node absent from the result means the browse failed permanently for that node
    /// (transient failures throw <see cref="OpcUaTransientServiceException"/>).
    /// </summary>
    private async Task<Dictionary<NodeId, IReadOnlyList<ReferenceDescription>>> BrowseAsync(
        IReadOnlyCollection<NodeId> nodeIds,
        ISession session,
        CancellationToken cancellationToken)
    {
        var view = new Dictionary<NodeId, IReadOnlyList<ReferenceDescription>>(nodeIds.Count);
        List<NodeId>? missing = null;
        foreach (var nodeId in nodeIds)
        {
            if (view.ContainsKey(nodeId))
            {
                continue;
            }

            if (_browseCache.TryGetValue(nodeId, out var cached))
            {
                view[nodeId] = cached;
            }
            else
            {
                (missing ??= new List<NodeId>(nodeIds.Count)).Add(nodeId);
            }
        }

        if (missing is { Count: > 0 })
        {
            var results = await session.BrowseNodesAsync(
                missing,
                _configuration.MaxReferencesPerNode,
                _configuration.MaxBrowseContinuations,
                _logger,
                cancellationToken).ConfigureAwait(false);

            foreach (var (nodeId, refs) in results)
            {
                _browseCache[nodeId] = refs;
                view[nodeId] = refs;
            }
        }

        return view;
    }

    private static string ExtractDictionaryKey(string browseName)
    {
        return OpcUaBrowseName.TryGetBracketContent(browseName, out var content)
            ? content.ToString()
            : browseName;
    }

    // -----------------------------------------------------------------------------------------
    // Private value types
    // -----------------------------------------------------------------------------------------

    private readonly record struct ChildEntry(
        ReferenceDescription Reference,
        NodeId ResolvedNodeId,
        RegisteredSubjectProperty? Property);

    private readonly record struct SubjectState(
        IInterceptorSubject Subject,
        RegisteredSubject Registered,
        List<ChildEntry> ChildEntries);

    private readonly record struct PendingSubjectRef(
        RegisteredSubjectProperty Property,
        ReferenceDescription NodeReference,
        IInterceptorSubject SubjectToLoad,
        bool IsNew);

    private readonly record struct DynamicAttributeNode(
        RegisteredSubjectProperty OwnerProperty,
        NodeId ChildNodeId,
        ReferenceDescription ChildNode,
        string BrowseName);
}
