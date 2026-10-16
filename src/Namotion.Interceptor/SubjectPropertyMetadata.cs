using System.Reflection;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor;

public readonly record struct SubjectPropertyMetadata
{
    /// <summary>
    /// Gets the name of the property.
    /// </summary>
    public string Name { get; }
    
    /// <summary>
    /// Gets the type of the property.
    /// </summary>
    public Type Type { get; }
    
    /// <summary>
    /// Gets the reflection attributes defined on the property.
    /// </summary>
    public IReadOnlyCollection<Attribute> Attributes { get; }
    
    // TODO(perf): Use generic instead of object? here?
    
    /// <summary>
    /// Gets the getter delegate for the property.
    /// </summary>
    public Func<IInterceptorSubject, object?>? GetValue { get; }
    
    /// <summary>
    /// Gets the setter delegate for the property.
    /// </summary>
    public Action<IInterceptorSubject, object?>? SetValue { get; }

    /// <summary>
    /// Gets a value indicating whether the property is intercepted (dynamic, manually handled or marked was partial).
    /// </summary>
    public bool IsIntercepted { get; }

    /// <summary>
    /// Gets a value indicating whether the property is dynamic (not backed by native property and backed by PropertyInfo).
    /// </summary>
    public bool IsDynamic { get; }

    /// <summary>
    /// Gets a value indicating whether the property is marked as derived (has DerivedAttribute).
    /// </summary>
    public bool IsDerived { get; }
    
    /// <summary>
    /// Gets a value indicating whether the getter or setter of the property is public (true for dynamic properties).
    /// </summary>
    public bool IsPublic { get; }

    /// <summary>
    /// Gets the PropertyInfo for the property, if available.
    /// </summary>
    public PropertyInfo? PropertyInfo { get; }

    // A Func<IInterceptorSubject, TProperty> reading the backing store without interception. The terminal
    // write invokes it while holding the subject's SyncRoot, so it must not take locks or run interceptors.
    internal Delegate? ReadStoredValue { get; }

    /// <summary>
    /// Returns a copy that carries a plain read of the property's backing store. A committed write through
    /// the interceptor chain then reports the value the store replaced, read with it under the subject lock
    /// immediately before the store, instead of the value the caller passed. Generated partial properties
    /// carry one; registry and dynamic properties do not.
    /// </summary>
    /// <remarks>
    /// The reader runs under the subject's SyncRoot, so it must read the store directly, without locks or
    /// interception. It applies to a write whose <c>TProperty</c> the reader is assignable to: the declared
    /// type, or <c>object</c> for a reference-type property. A boxed write of a value-type property and a
    /// derived recalculation keep the caller's value.
    /// </remarks>
    public SubjectPropertyMetadata WithStoredValueReader<TProperty>(Func<IInterceptorSubject, TProperty> readStoredValue)
    {
        return new SubjectPropertyMetadata(this, readStoredValue);
    }

    public SubjectPropertyMetadata(
        PropertyInfo propertyInfo, 
        Func<IInterceptorSubject, object?>? getValue, 
        Action<IInterceptorSubject, object?>? setValue, 
        bool isIntercepted, 
        bool isDynamic)
        : this(
            propertyInfo.Name,
            propertyInfo.PropertyType,
            propertyInfo.GetCustomAttributesIncludingInterfaces(),
            getValue,
            setValue,
            isIntercepted,
            isDynamic,
            propertyInfo)
    {
    }

    public SubjectPropertyMetadata(
        string name, 
        Type type, 
        IReadOnlyCollection<Attribute> attributes, 
        Func<IInterceptorSubject, object?>? getValue, 
        Action<IInterceptorSubject, object?>? setValue,
        bool isIntercepted,
        bool isDynamic) : this(
            name,
            type,
            attributes,
            getValue,
            setValue,
            isIntercepted,
            isDynamic,
            propertyInfo: null)
    {
    }
    
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S107", Justification = "The private constructor combines the existing public metadata shapes without an intermediate allocation.")]
    private SubjectPropertyMetadata(
        string name, 
        Type type, 
        IReadOnlyCollection<Attribute> attributes, 
        Func<IInterceptorSubject, object?>? getValue, 
        Action<IInterceptorSubject, object?>? setValue,
        bool isIntercepted,
        bool isDynamic,
        PropertyInfo? propertyInfo)
    {
        Name = name;
        Type = type;
        Attributes = attributes;
        GetValue = getValue;
        SetValue = setValue;
        IsIntercepted = isIntercepted;
        IsDynamic = isDynamic;
        IsDerived = attributes.Any(a => a is DerivedAttribute);
        PropertyInfo = propertyInfo;
        IsPublic =
            PropertyInfo is null ||
            PropertyInfo.GetMethod?.IsPublic == true ||
            PropertyInfo.SetMethod?.IsPublic == true;
    }

    private SubjectPropertyMetadata(in SubjectPropertyMetadata source, Delegate readStoredValue)
    {
        Name = source.Name;
        Type = source.Type;
        Attributes = source.Attributes;
        GetValue = source.GetValue;
        SetValue = source.SetValue;
        IsIntercepted = source.IsIntercepted;
        IsDynamic = source.IsDynamic;
        IsDerived = source.IsDerived;
        PropertyInfo = source.PropertyInfo;
        IsPublic = source.IsPublic;
        ReadStoredValue = readStoredValue;
    }
}