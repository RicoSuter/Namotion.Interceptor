using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Loads OPC UA attribute nodes (both attributes declared on the C# model and dynamic attributes
/// discovered at runtime) for a batch of variable nodes. Drives the multi-round traversal that
/// walks attributes of attributes until no new nodes appear or the configured limit is reached.
/// Holds a back-reference to the owning <see cref="OpcUaSubjectLoader"/> so monitored-item
/// claims go through the same ownership path as the rest of the loader. Has no mutable
/// instance state; all per-load state lives in <see cref="OpcUaLoadContext"/> or method locals.
/// </summary>
internal sealed class OpcUaAttributeLoader
{
    private readonly IInterceptorSubject _subject;
    private readonly OpcUaSubjectLoader _loader;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    public OpcUaAttributeLoader(
        IInterceptorSubject subject,
        OpcUaSubjectLoader loader,
        OpcUaClientConfiguration configuration,
        ILogger logger)
    {
        _subject = subject;
        _loader = loader;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Candidate dynamic attribute discovered in a round, deferred until the batch
    /// type-resolution step at the end of the round.
    /// </summary>
    private readonly record struct DynamicAttributeNode(
        RegisteredSubjectProperty OwnerProperty,
        NodeId ChildNodeId,
        ReferenceDescription ChildNode,
        string BrowseName);

    /// <summary>
    /// Runs the multi-round attribute traversal over <paramref name="variableNodes"/>: each round
    /// browses the current variable nodes, matches known attributes and (when configured) adds
    /// dynamic attributes, then feeds those attributes back as the next round so attributes-of-
    /// attributes are discovered until no new nodes appear or <see cref="OpcUaClientConfiguration.MaxAttributeTraversals"/>
    /// is reached.
    /// </summary>
    public async Task LoadAttributesAsync(
        IReadOnlyList<(RegisteredSubjectProperty Property, NodeId NodeId)> variableNodes,
        OpcUaLoadContext context)
    {
        if (variableNodes.Count == 0)
        {
            return;
        }

        // Tracks (property, parentNodeId) pairs already processed in any prior round, so
        // we never call MatchKnownAttributes / CollectDynamicAttributesAsync twice for the
        // same pair. A property could legitimately appear in multiple rounds against
        // different parent NodeIds, but processing the SAME pair twice would re-claim
        // already-monitored sub-attribute references.
        var processedEntries = new HashSet<(RegisteredSubjectProperty Property, NodeId ParentNodeId)>();
        var currentRound = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>(variableNodes);

        var traversal = 0;
        while (currentRound.Count > 0)
        {
            // A round served entirely from the browse cache awaits no I/O, so check here to stay
            // responsive to cancellation between rounds.
            context.CancellationToken.ThrowIfCancellationRequested();

            if (++traversal > _configuration.MaxAttributeTraversals)
            {
                _logger.LogWarning(
                    "Aborting attribute traversal after {MaxTraversals} levels with {Remaining} entries still pending. Possible cycle in address space or attribute registration.",
                    _configuration.MaxAttributeTraversals, currentRound.Count);
                break;
            }

            currentRound = await ProcessAttributeRoundAsync(currentRound, processedEntries, context).ConfigureAwait(false);
        }
    }

    private async Task<List<(RegisteredSubjectProperty Property, NodeId NodeId)>> ProcessAttributeRoundAsync(
        List<(RegisteredSubjectProperty Property, NodeId ParentNodeId)> currentRound,
        HashSet<(RegisteredSubjectProperty Property, NodeId ParentNodeId)> processedEntries,
        OpcUaLoadContext context)
    {
        var parentIds = new HashSet<NodeId>(currentRound.Count);
        foreach (var (_, parentNodeId) in currentRound)
        {
            parentIds.Add(parentNodeId);
        }

        var browseResults = await context.BrowseAsync(parentIds).ConfigureAwait(false);

        // Memoize per parent so two entries sharing a parent (alias case) don't recompute.
        var distinctChildren = new Dictionary<NodeId, List<(ReferenceDescription Reference, NodeId NodeId)>>(browseResults.Count);
        foreach (var (parentNodeId, rawChildren) in browseResults)
        {
            distinctChildren[parentNodeId] = context.DistinctByResolvedNodeId(rawChildren);
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

            var processedBrowseNames = MatchKnownAttributes(property, childNodes, nextRound, context);
            await CollectDynamicAttributesAsync(property, parentNodeId, childNodes, processedBrowseNames, dynamicAttributeNodes, context).ConfigureAwait(false);
        }

        await CreateDynamicAttributesAsync(dynamicAttributeNodes, nextRound, context).ConfigureAwait(false);
        return nextRound;
    }

    private HashSet<string> MatchKnownAttributes(
        RegisteredSubjectProperty property,
        List<(ReferenceDescription Reference, NodeId NodeId)> childNodes,
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> nextRound,
        OpcUaLoadContext context)
    {
        // childNodes have non-null BrowseName.Name (filtered at the session boundary in DistinctByResolvedNodeId).
        var childByBrowseName = new Dictionary<string, NodeId>(childNodes.Count);
        foreach (var (childNode, childNodeId) in childNodes)
        {
            childByBrowseName.TryAdd(childNode.BrowseName.Name, childNodeId);
        }

        var processedBrowseNames = new HashSet<string>();
        foreach (var attribute in property.Attributes)
        {
            if (!_configuration.Mapper.TryGetMapping(attribute, _subject, out var attributeConfiguration))
            {
                continue;
            }

            var attributeBrowseName = attributeConfiguration.BrowseName ?? attribute.BrowseName;
            if (!childByBrowseName.TryGetValue(attributeBrowseName, out var matchingNodeId))
            {
                continue;
            }

            processedBrowseNames.Add(attributeBrowseName);
            _loader.MonitorValueNode(matchingNodeId, attribute, context);
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
        OpcUaLoadContext context)
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
                await _configuration.ShouldAddDynamicAttribute(childNode, context.CancellationToken).ConfigureAwait(false);
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
        OpcUaLoadContext context)
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
        var resolvedTypes = await _configuration.TypeResolver!.ResolveVariableTypesAsync(context.Session, variableReferences, context.CancellationToken).ConfigureAwait(false);

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
            // sibling parents with identical BrowseNames but different NodeIds resolve to
            // the same owner property, and the TryGetAttribute pre-check in
            // CollectDynamicAttributesAsync runs before any attribute is created. Re-check
            // here so the duplicate is skipped instead of crashing AddAttribute with a
            // duplicate-key exception (which would abort the load and recur on every retry).
            if (entry.OwnerProperty.TryGetAttribute(entry.BrowseName) is not null)
            {
                _logger.LogWarning(
                    "Skipping dynamic attribute '{AttributeName}' on property '{PropertyName}' (NodeId: {NodeId}): an attribute with this name was already created by a sibling entry in this round.",
                    entry.BrowseName, entry.OwnerProperty.Name, entry.ChildNode.NodeId);
                continue;
            }

            object? value = null;
            // Like TryCreateDynamicProperty: added eagerly, so a leftover from a failed load
            // survives, but MatchKnownAttributes re-matches it on the next load (via its
            // attached OpcUaNodeAttribute mapping) and re-monitors it.
            var dynamicAttribute = entry.OwnerProperty.AddAttribute(
                entry.BrowseName,
                inferredType,
                _ => value,
                (_, o) => value = o,
                _configuration.TypeResolver!.GetDynamicPropertyAttributes(entry.ChildNode, context.Session));

            _loader.MonitorValueNode(entry.ChildNodeId, dynamicAttribute, context);
            nextRound.Add((dynamicAttribute, entry.ChildNodeId));
        }
    }
}
