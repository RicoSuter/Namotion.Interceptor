using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;
using Castle.DynamicProxy;

namespace Namotion.Interceptor.Dynamic;

public class DynamicSubject : IInterceptorSubject, IInterceptor
{
    private readonly ConcurrentDictionary<string, object?> _propertyValues = new();
    private IInterceptorExecutor? _context;

    [JsonIgnore]
    IInterceptorSubjectContext IInterceptorSubject.Context => _context ??= new InterceptorExecutor(this);

    [JsonIgnore]
    ConcurrentDictionary<string, object?> IInterceptorSubject.Data { get; } = new();

    [JsonIgnore]
    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

    private static IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties = new Dictionary<string, SubjectPropertyMetadata>
    {
    };

    public static DynamicSubject Create(IInterceptorSubjectContext? context, params Type[] interfaces)
    {
        var subject = new DynamicSubject();
        var generator = new ProxyGenerator();

        // Create a proxy that implements IAbc + (additional) IDef
        var subjectProxy = (DynamicSubject)generator.CreateClassProxyWithTarget(
            typeof(DynamicSubject),
            interfaces,
            subject,
            [subject]
        );
        
        // TODO: Initialize _properties
        
        if (context is not null)
        {
            ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
        }

        return subjectProxy;
    }
    
    public void Intercept(IInvocation invocation)
    {
        if (invocation.Method.IsSpecialName &&
            invocation.Method.Name.StartsWith("get_"))
        {
            var propertyName = invocation.Method.Name[4..];
            var propertyType = invocation.Method.ReturnType;

            var value = _context is not null ? 
                _context.GetPropertyValue(propertyName, _ => ReadProperty(propertyName, propertyType)) : 
                ReadProperty(propertyName, propertyType);

            invocation.ReturnValue = value;
        }
        else if (invocation.Method.IsSpecialName &&
            invocation.Method.Name.StartsWith("set_"))
        {
            var propertyName = invocation.Method.Name[4..];
            var newValue = invocation.Arguments[0];

            if (_context is not null)
            {
                var propertyType = invocation.Method.GetParameters().Single().ParameterType;
                _context.SetPropertyValue(propertyName, newValue,
                    _ => ReadProperty(propertyName, propertyType),
                    (_, value) => WriteProperty(propertyName, value));
            }
            else
            {
                WriteProperty(propertyName, newValue);
            }

            invocation.ReturnValue = null;
        }
        else
        {
            if (_context is not null)
            {
                // TODO: Add tests
                invocation.ReturnValue = _context.InvokeMethod(
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

    private object? ReadProperty(string propertyName, Type propertyType)
    {
        var value = _propertyValues.GetOrAdd(propertyName, 
            _ => propertyType.IsValueType ? Activator.CreateInstance(propertyType) : null);
        return value;
    }

    private void WriteProperty(string propertyName, object? newValue)
    {
        _propertyValues[propertyName] = newValue;
    }
}