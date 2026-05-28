using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Maps properties using code-based fluent configuration.
/// Supports instance-based configuration (different config for Motor1.Speed vs Motor2.Speed).
/// </summary>
/// <typeparam name="T">The root type to configure.</typeparam>
public class OpcUaFluentMapper<T> : IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>
{
    private readonly ConcurrentDictionary<string, OpcUaPropertyMapping> _mappings = new();

    /// <summary>
    /// Maps a property with fluent configuration.
    /// </summary>
    public OpcUaFluentMapper<T> Map<TProperty>(
        Expression<Func<T, TProperty>> propertySelector,
        Action<IPropertyBuilder<TProperty>> configure)
    {
        var path = GetPropertyPath(propertySelector);
        var builder = new PropertyBuilder<TProperty>(path, _mappings);
        configure(builder);
        return this;
    }

    /// <inheritdoc />
    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out OpcUaPropertyMapping? mapping)
    {
        var path = GetPropertyPath(property, rootSubject);
        if (_mappings.TryGetValue(path, out var stored))
        {
            mapping = stored;
            return true;
        }
        mapping = null;
        return false;
    }

    /// <inheritdoc />
    public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        OpcUaLookupKey key,
        RegisteredSubject rootSubject,
        CancellationToken cancellationToken)
    {
        var browseName = key.Reference.BrowseName.Name;

        foreach (var property in rootSubject.Properties)
        {
            if (property.IsAttribute)
                continue;

            if (TryGetMapping(property, rootSubject.Subject, out var config) && config.BrowseName == browseName)
            {
                return new ValueTask<RegisteredSubjectProperty?>(property);
            }
        }

        return new ValueTask<RegisteredSubjectProperty?>(result: null);
    }

    private static string GetPropertyPath<TProperty>(Expression<Func<T, TProperty>> expression) =>
        ExpressionPathHelper.GetPathFromExpression(expression.Body);

    private static string GetPropertyPath(RegisteredSubjectProperty property, IInterceptorSubject? rootSubject = null) =>
        property.GetPath(rootSubject: rootSubject);

    private class PropertyBuilder<TProp> : IPropertyBuilder<TProp>
    {
        private readonly string _basePath;
        private readonly ConcurrentDictionary<string, OpcUaPropertyMapping> _mappings;
        private OpcUaPropertyMapping _config = new();

        public PropertyBuilder(string basePath, ConcurrentDictionary<string, OpcUaPropertyMapping> mappings)
        {
            _basePath = basePath;
            _mappings = mappings;
            _mappings[basePath] = _config;
        }

        private IPropertyBuilder<TProp> UpdateConfig(Func<OpcUaPropertyMapping, OpcUaPropertyMapping> update)
        {
            _config = _mappings.AddOrUpdate(
                _basePath,
                _ => update(new OpcUaPropertyMapping()),
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

        public IPropertyBuilder<TProp> TypeDefinition(string identifier, string? namespaceUri = null) =>
            UpdateConfig(c => c with { TypeDefinition = identifier, TypeDefinitionNamespace = namespaceUri });

        public IPropertyBuilder<TProp> NodeClass(OpcUaNodeClass value) =>
            UpdateConfig(c => c with { NodeClass = value });

        public IPropertyBuilder<TProp> DataType(string identifier, string? namespaceUri = null) =>
            UpdateConfig(c => c with { DataType = identifier, DataTypeNamespace = namespaceUri });

        public IPropertyBuilder<TProp> IsValue(bool value = true) =>
            UpdateConfig(c => c with { IsValue = value });

        public IPropertyBuilder<TProp> ReferenceType(string identifier, string? namespaceUri = null) =>
            UpdateConfig(c => c with { ReferenceType = identifier, ReferenceTypeNamespace = namespaceUri });

        public IPropertyBuilder<TProp> ItemReferenceType(string identifier, string? namespaceUri = null) =>
            UpdateConfig(c => c with { ItemReferenceType = identifier, ItemReferenceTypeNamespace = namespaceUri });

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
            string? referenceTypeNamespace,
            string targetNodeId,
            string? targetNamespaceUri = null,
            bool isForward = true)
        {
            var newRef = new OpcUaAdditionalReference
            {
                ReferenceType = referenceType,
                ReferenceTypeNamespace = referenceTypeNamespace,
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
            var relativePath = ExpressionPathHelper.GetPathFromExpression(propertySelector.Body);
            var fullPath = $"{_basePath}.{relativePath}";
            var builder = new PropertyBuilder<TProperty>(fullPath, _mappings);
            configure(builder);
            return this;
        }
    }
}
