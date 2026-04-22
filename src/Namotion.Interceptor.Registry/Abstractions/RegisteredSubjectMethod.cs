namespace Namotion.Interceptor.Registry.Abstractions;

public class RegisteredSubjectMethod : RegisteredSubjectMember
{
    private readonly Func<IInterceptorSubject, object?[], object?> _invoke;

    internal RegisteredSubjectMethod(
        RegisteredSubject parent,
        string name,
        SubjectMethodMetadata metadata)
        : base(parent, name, metadata.Attributes)
    {
        ReturnType = metadata.ReturnType;
        Parameters = metadata.Parameters;
        IsIntercepted = metadata.IsIntercepted;
        IsDynamic = metadata.IsDynamic;
        IsPublic = metadata.IsPublic;
        _invoke = metadata.Invoke;
    }

    /// <summary>
    /// Gets the return type of the method.
    /// </summary>
    public Type ReturnType { get; }

    /// <summary>
    /// Gets the parameter metadata for the method.
    /// </summary>
    public IReadOnlyList<SubjectMethodParameterMetadata> Parameters { get; }

    /// <summary>
    /// Gets a value indicating whether this method is intercepted (WithoutInterceptor pattern).
    /// </summary>
    public bool IsIntercepted { get; }

    /// <summary>
    /// Gets a value indicating whether this method was dynamically added at runtime.
    /// </summary>
    public bool IsDynamic { get; }

    /// <summary>
    /// Gets a value indicating whether this method is publicly accessible.
    /// Connectors can use this flag to filter non-public methods out of protocol exposure.
    /// </summary>
    public bool IsPublic { get; }

    /// <summary>
    /// Invokes the method with the specified parameters.
    /// </summary>
    /// <param name="parameters">The method parameters.</param>
    /// <returns>The return value, or null for void methods.</returns>
    public object? Invoke(object?[] parameters) => _invoke(Parent.Subject, parameters);

    /// <summary>
    /// Runs method initializers for this method. Invoked outside registry locks
    /// so initializers can safely call back into the registry.
    /// </summary>
    internal void RunInitializers()
    {
        foreach (var attribute in ReflectionAttributes.OfType<ISubjectMethodInitializer>())
        {
            attribute.InitializeMethod(this);
        }

        foreach (var initializer in Parent.Subject.Context.GetServices<ISubjectMethodInitializer>())
        {
            initializer.InitializeMethod(this);
        }
    }
}
