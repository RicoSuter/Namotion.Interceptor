namespace Namotion.Interceptor;

/// <summary>
/// Describes a parameter of a subject method discovered by the source generator
/// or added dynamically via <c>RegisteredSubject.AddMethod</c>.
/// </summary>
public readonly record struct SubjectMethodParameterMetadata
{
    /// <summary>
    /// Gets the parameter name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the parameter type.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// Gets the .NET reflection attributes declared on the parameter.
    /// </summary>
    public IReadOnlyCollection<Attribute> Attributes { get; }

    /// <summary>
    /// Initializes a new <see cref="SubjectMethodParameterMetadata"/>.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="type">The parameter type.</param>
    /// <param name="attributes">The .NET reflection attributes declared on the parameter.</param>
    public SubjectMethodParameterMetadata(
        string name,
        Type type,
        IReadOnlyCollection<Attribute> attributes)
    {
        Name = name;
        Type = type;
        Attributes = attributes;
    }
}

/// <summary>
/// Describes a subject method registered with the interceptor subject, including
/// its signature, reflection attributes, and an invocation delegate. Used by the
/// Registry to expose methods as first-class members alongside properties.
/// </summary>
public readonly record struct SubjectMethodMetadata
{
    /// <summary>
    /// Gets the method name (as registered in <see cref="IInterceptorSubject.Methods"/>).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the method return type.
    /// </summary>
    public Type ReturnType { get; }

    /// <summary>
    /// Gets the method parameters.
    /// </summary>
    public IReadOnlyList<SubjectMethodParameterMetadata> Parameters { get; }

    /// <summary>
    /// Gets the .NET reflection attributes declared on the method.
    /// </summary>
    public IReadOnlyCollection<Attribute> Attributes { get; }

    /// <summary>
    /// Gets the invocation delegate. Takes the subject instance and a parameter
    /// array (in declared order) and returns the method result (or <c>null</c>
    /// for void methods).
    /// </summary>
    public Func<IInterceptorSubject, object?[], object?> Invoke { get; }

    /// <summary>
    /// Gets a value indicating whether the method uses the WithoutInterceptor
    /// pattern and is intercepted on invocation.
    /// </summary>
    public bool IsIntercepted { get; }

    /// <summary>
    /// Gets a value indicating whether the method was added dynamically at
    /// runtime (as opposed to being discovered by the source generator).
    /// </summary>
    public bool IsDynamic { get; }

    /// <summary>
    /// Gets a value indicating whether the method is publicly accessible.
    /// Connectors can use this flag to filter non-public methods out of
    /// protocol exposure. Dynamic methods are always treated as public
    /// because they are registered explicitly by consumers.
    /// Only <c>public</c> methods return true. <c>internal</c>, <c>protected</c>, <c>private</c>,
    /// and combinations like <c>protected internal</c> or <c>private protected</c> all return false.
    /// </summary>
    public bool IsPublic { get; }

    /// <summary>
    /// Initializes a new <see cref="SubjectMethodMetadata"/>.
    /// </summary>
    /// <param name="name">The method name.</param>
    /// <param name="returnType">The method return type.</param>
    /// <param name="parameters">The method parameters.</param>
    /// <param name="attributes">The .NET reflection attributes declared on the method.</param>
    /// <param name="invoke">The invocation delegate.</param>
    /// <param name="isIntercepted">Whether the method is intercepted on invocation.</param>
    /// <param name="isDynamic">Whether the method was added dynamically at runtime.</param>
    /// <param name="isPublic">Whether the method is publicly accessible.</param>
    public SubjectMethodMetadata(
        string name,
        Type returnType,
        IReadOnlyList<SubjectMethodParameterMetadata> parameters,
        IReadOnlyCollection<Attribute> attributes,
        Func<IInterceptorSubject, object?[], object?> invoke,
        bool isIntercepted,
        bool isDynamic,
        bool isPublic)
    {
        Name = name;
        ReturnType = returnType;
        Parameters = parameters;
        Attributes = attributes;
        Invoke = invoke;
        IsIntercepted = isIntercepted;
        IsDynamic = isDynamic;
        IsPublic = isPublic;
    }
}
