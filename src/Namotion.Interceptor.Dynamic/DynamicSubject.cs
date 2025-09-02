using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json.Serialization;
using Castle.DynamicProxy;

namespace Namotion.Interceptor.Dynamic;

public class DynamicSubject : IInterceptorSubject
{
    private static readonly ProxyGenerator ProxyGenerator = new();
    
    private readonly ConcurrentDictionary<string, object?> _propertyValues = new();
    private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties 
        = ReadOnlyDictionary<string, SubjectPropertyMetadata>.Empty;

    private IInterceptorExecutor? _context;

    [JsonIgnore]
    IInterceptorSubjectContext IInterceptorSubject.Context => _context ??= new InterceptorExecutor(this);

    [JsonIgnore]
    ConcurrentDictionary<string, object?> IInterceptorSubject.Data { get; } = new();

    [JsonIgnore]
    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
    {
        _properties = _properties
            .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
            .ToFrozenDictionary();
    }

    public static DynamicSubject Create(IInterceptorSubjectContext? context, params Type[] interfaces)
    {
        return Create<DynamicSubject>(context, interfaces);
    }

    public static TSubject Create<TSubject>(IInterceptorSubjectContext? context, params Type[] interfaces)
        where TSubject : IInterceptorSubject
    {
        return (TSubject)Create(typeof(TSubject), context, interfaces);
    }

    public static IInterceptorSubject Create(Type type, IInterceptorSubjectContext? context, params Type[] interfaces)
    {
        // TODO: Should we allow any IInterceptorSubject based class?
        // How to replace properties? Can we make it replaceable and also use for dynamic properties?

        var subject = (IInterceptorSubject)ProxyGenerator.CreateClassProxy(
            type,
            interfaces,
            [new DynamicSubjectInterceptor()]
        );
        
        // TODO(perf): Cache and reuse properties for (TDynamicSubject, interfaces)

        subject.AddProperties(subject
            .GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .DistinctBy(p => p.Name)
            .Select(property => new SubjectPropertyMetadata(
                property.Name,
                property.PropertyType,
                property.GetCustomAttributes().ToArray(),
                property.GetValue,
                property.SetValue,
                isIntercepted: true,
                isDynamic: false)));

        if (context is not null)
        {
            ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
        }

        return subject;
    }

    private object? ReadProperty(string propertyName, Type propertyType)
    {
        return _propertyValues.GetOrAdd(propertyName, 
            _ => propertyType.IsValueType ? Activator.CreateInstance(propertyType) : null);
    }

    private void WriteProperty(string propertyName, object? newValue)
    {
        _propertyValues[propertyName] = newValue;
    }

    private class DynamicSubjectInterceptor : IInterceptor
    {
        public void Intercept(IInvocation invocation)
        {
            var subject = (DynamicSubject)invocation.Proxy;
            var context = subject._context;
            
            if (invocation.Method.IsSpecialName &&
                invocation.Method.Name.StartsWith("get_"))
            {
                var propertyName = invocation.Method.Name[4..];
                var propertyType = invocation.Method.ReturnType;

                var value = context is not null ? 
                    context.GetPropertyValue(propertyName, _ => subject.ReadProperty(propertyName, propertyType)) : 
                    subject.ReadProperty(propertyName, propertyType);

                invocation.ReturnValue = value;
            }
            else if (invocation.Method.IsSpecialName &&
                invocation.Method.Name.StartsWith("set_"))
            {
                var propertyName = invocation.Method.Name[4..];
                var newValue = invocation.Arguments[0];

                if (context is not null)
                {
                    var propertyType = invocation.Method.GetParameters().Single().ParameterType;
                    context.SetPropertyValue(propertyName, newValue,
                        _ => subject.ReadProperty(propertyName, propertyType),
                        (_, value) => subject.WriteProperty(propertyName, value));
                }
                else
                {
                    subject.WriteProperty(propertyName, newValue);
                }

                invocation.ReturnValue = null;
            }
            else
            {
                if (context is not null)
                {
                    invocation.ReturnValue = context.InvokeMethod(
                        invocation.Method.Name, invocation.Arguments, parameters =>
                        {
                            parameters.CopyTo(invocation.Arguments, 0);
                            invocation.Proceed();
                            return invocation.ReturnValue;
                        });
                }
                else
                {
                    invocation.Proceed();
                }
            }
        }
    }
}