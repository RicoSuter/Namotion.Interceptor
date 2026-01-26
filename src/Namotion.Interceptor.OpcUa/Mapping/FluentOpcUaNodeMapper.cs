using System.Collections.Concurrent;
using System.Linq.Expressions;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Maps properties using code-based fluent configuration.
/// Supports instance-based configuration (different config for Motor1.Speed vs Motor2.Speed).
/// </summary>
/// <typeparam name="T">The root type to configure.</typeparam>
public class FluentOpcUaNodeMapper<T> : IOpcUaNodeMapper
{
    private readonly ConcurrentDictionary<string, OpcUaNodeConfiguration> _mappings = new();

    /// <summary>
    /// Maps a property with fluent configuration.
    /// </summary>
    public FluentOpcUaNodeMapper<T> Map<TProperty>(
        Expression<Func<T, TProperty>> propertySelector,
        Action<IPropertyBuilder<TProperty>> configure)
    {
        var path = GetPropertyPath(propertySelector);
        var builder = new PropertyBuilder<TProperty>(path, _mappings);
        configure(builder);
        return this;
    }

    /// <inheritdoc />
    public OpcUaNodeConfiguration? TryGetNodeConfiguration(RegisteredSubjectProperty property)
    {
        var path = GetPropertyPath(property);
        return _mappings.GetValueOrDefault(path);
    }

    /// <inheritdoc />
    public Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        var browseName = nodeReference.BrowseName.Name;

        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute)
                continue;

            var path = GetPropertyPath(property);
            if (_mappings.TryGetValue(path, out var config) && config.BrowseName == browseName)
            {
                return Task.FromResult<RegisteredSubjectProperty?>(property);
            }
        }

        return Task.FromResult<RegisteredSubjectProperty?>(null);
    }

    private static string GetPropertyPath<TProperty>(Expression<Func<T, TProperty>> expression) =>
        GetPropertyPathFromExpression(expression.Body);

    /// <summary>
    /// Extracts property path from an expression body by traversing member access chain.
    /// </summary>
    private static string GetPropertyPathFromExpression(Expression expression)
    {
        var parts = new List<string>();
        var current = expression;

        while (current is MemberExpression member)
        {
            parts.Insert(0, member.Member.Name);
            current = member.Expression;
        }

        return string.Join(".", parts);
    }

    /// <summary>
    /// Builds the property path by traversing parent references.
    /// When an object has multiple parents (e.g., same instance referenced from multiple properties),
    /// the first parent registration is used to determine the path. This means fluent configuration
    /// should use the path from the first reference point in the object graph.
    /// </summary>
    private static string GetPropertyPath(RegisteredSubjectProperty property)
    {
        var parts = new List<string> { property.Name };
        var currentSubject = property.Parent;

        while (currentSubject.Parents.Length > 0)
        {
            // Use first parent when multiple exist (first registration wins)
            var parent = currentSubject.Parents[0];
            parts.Insert(0, parent.Property.Name);
            currentSubject = parent.Property.Parent;
        }

        return string.Join(".", parts);
    }

    private class PropertyBuilder<TProp> : IPropertyBuilder<TProp>
    {
        private readonly string _basePath;
        private readonly ConcurrentDictionary<string, OpcUaNodeConfiguration> _mappings;
        private OpcUaNodeConfiguration _config = new();

        public PropertyBuilder(string basePath, ConcurrentDictionary<string, OpcUaNodeConfiguration> mappings)
        {
            _basePath = basePath;
            _mappings = mappings;
            _mappings[basePath] = _config;
        }

        private IPropertyBuilder<TProp> UpdateConfig(Func<OpcUaNodeConfiguration, OpcUaNodeConfiguration> update)
        {
            _config = _mappings.AddOrUpdate(
                _basePath,
                _ => update(new OpcUaNodeConfiguration()),
                (_, existing) => update(existing));
            return this;
        }

        public IPropertyBuilder<TProp> BrowseName(string value) =>
            UpdateConfig(c => c with { BrowseName = value });

        public IPropertyBuilder<TProp> BrowseNamespaceUri(string value) =>
            UpdateConfig(c => c with { BrowseNamespaceUri = value });

        public IPropertyBuilder<TProp> NodeIdentifier(string value) =>
            UpdateConfig(c => c with { NodeIdentifier = value });

        public IPropertyBuilder<TProp> NodeNamespaceUri(string value) =>
            UpdateConfig(c => c with { NodeNamespaceUri = value });

        public IPropertyBuilder<TProp> DisplayName(string value) =>
            UpdateConfig(c => c with { DisplayName = value });

        public IPropertyBuilder<TProp> Description(string value) =>
            UpdateConfig(c => c with { Description = value });

        public IPropertyBuilder<TProp> TypeDefinition(string value) =>
            UpdateConfig(c => c with { TypeDefinition = value });

        public IPropertyBuilder<TProp> TypeDefinitionNamespace(string value) =>
            UpdateConfig(c => c with { TypeDefinitionNamespace = value });

        public IPropertyBuilder<TProp> NodeClass(OpcUaNodeClass value) =>
            UpdateConfig(c => c with { NodeClass = value });

        public IPropertyBuilder<TProp> DataType(string value) =>
            UpdateConfig(c => c with { DataType = value });

        public IPropertyBuilder<TProp> IsValue(bool value = true) =>
            UpdateConfig(c => c with { IsValue = value });

        public IPropertyBuilder<TProp> ReferenceType(string value) =>
            UpdateConfig(c => c with { ReferenceType = value });

        public IPropertyBuilder<TProp> ItemReferenceType(string value) =>
            UpdateConfig(c => c with { ItemReferenceType = value });

        public IPropertyBuilder<TProp> SamplingInterval(int value) =>
            UpdateConfig(c => c with { SamplingInterval = value });

        public IPropertyBuilder<TProp> QueueSize(uint value) =>
            UpdateConfig(c => c with { QueueSize = value });

        public IPropertyBuilder<TProp> DiscardOldest(bool value) =>
            UpdateConfig(c => c with { DiscardOldest = value });

        public IPropertyBuilder<TProp> DataChangeTrigger(DataChangeTrigger value) =>
            UpdateConfig(c => c with { DataChangeTrigger = value });

        public IPropertyBuilder<TProp> DeadbandType(DeadbandType value) =>
            UpdateConfig(c => c with { DeadbandType = value });

        public IPropertyBuilder<TProp> DeadbandValue(double value) =>
            UpdateConfig(c => c with { DeadbandValue = value });

        public IPropertyBuilder<TProp> ModellingRule(ModellingRule value) =>
            UpdateConfig(c => c with { ModellingRule = value });

        public IPropertyBuilder<TProp> EventNotifier(byte value) =>
            UpdateConfig(c => c with { EventNotifier = value });

        public IPropertyBuilder<TProp> AdditionalReference(
            string referenceType,
            string targetNodeId,
            string? targetNamespaceUri = null,
            bool isForward = true)
        {
            var newRef = new OpcUaAdditionalReference
            {
                ReferenceType = referenceType,
                TargetNodeId = targetNodeId,
                TargetNamespaceUri = targetNamespaceUri,
                IsForward = isForward
            };
            return UpdateConfig(c => c with
            {
                AdditionalReferences = [.. (c.AdditionalReferences ?? []), newRef]
            });
        }

        public IPropertyBuilder<TProp> Map<TProperty>(
            Expression<Func<TProp, TProperty>> propertySelector,
            Action<IPropertyBuilder<TProperty>> configure)
        {
            var relativePath = GetPropertyPathFromExpression(propertySelector.Body);
            var fullPath = $"{_basePath}.{relativePath}";
            var builder = new PropertyBuilder<TProperty>(fullPath, _mappings);
            configure(builder);
            return this;
        }
    }
}
