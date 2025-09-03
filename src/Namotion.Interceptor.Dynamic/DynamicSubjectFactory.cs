using System.Collections.Concurrent;
using System.Reflection;
using Castle.DynamicProxy;

namespace Namotion.Interceptor.Dynamic;

public class DynamicSubjectFactory
{
    private static readonly ProxyGenerator ProxyGenerator = new();
    private static readonly ConcurrentDictionary<string, SubjectPropertyMetadata[]> PropertyCache = new();

    public static DynamicSubject CreateDynamicSubject(IInterceptorSubjectContext? context, params Type[] interfaces)
    {
        return CreateSubject<DynamicSubject>(context, interfaces);
    }

    public static TSubject CreateSubject<TSubject>(IInterceptorSubjectContext? context, params Type[] interfaces)
        where TSubject : IInterceptorSubject
    {
        return (TSubject)CreateSubject(context, typeof(TSubject), interfaces);
    }
    
    public static IInterceptorSubject CreateSubject(IInterceptorSubjectContext? context, Type type, params Type[] interfaces)
    {
        var subject = (IInterceptorSubject)ProxyGenerator
            .CreateClassProxy(type, interfaces, new DynamicSubjectInterceptor());
        
        var key = type.FullName + "|" + string.Join("|", interfaces.Select(i => i.FullName));
        var missingProperties = PropertyCache.GetOrAdd(key, static (_, newSubject) =>
        {
            var existingProperties = newSubject.Properties.Values;
            return newSubject
                .GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(p => existingProperties.All(ep => ep.Name != p.Name))
                .DistinctBy(p => p.Name)
                .Select(property => new SubjectPropertyMetadata(
                    property.Name,
                    property.PropertyType,
                    property.GetCustomAttributes().ToArray(),
                    property.GetValue,
                    property.SetValue,
                    isIntercepted: true,
                    isDynamic: false))
                .ToArray();
        }, subject);

        subject.AddProperties(missingProperties);
        
        if (context is not null)
        {
            subject.Context.AddFallbackContext(context);
        }

        return subject;
    }

    private class DynamicSubjectInterceptor : IInterceptor
    {
        private readonly ConcurrentDictionary<string, object?> _propertyValues = new();

        public void Intercept(IInvocation invocation)
        {
            if (invocation.MethodInvocationTarget is not null)
            {
                invocation.Proceed();
                return;
            }
            
            var subject = (IInterceptorSubject)invocation.Proxy;
            var context = (IInterceptorExecutor)subject.Context;

            if (invocation.Method.IsSpecialName &&
                invocation.Method.Name.StartsWith("get_"))
            {
                var propertyName = invocation.Method.Name[4..];
                var propertyType = invocation.Method.ReturnType;

                var value = context.GetPropertyValue(propertyName, _ => ReadProperty(propertyName, propertyType));

                invocation.ReturnValue = value;
            }
            else if (invocation.Method.IsSpecialName &&
                     invocation.Method.Name.StartsWith("set_"))
            {
                var propertyName = invocation.Method.Name[4..];
                var newValue = invocation.Arguments[0];
                var propertyType = invocation.Method.GetParameters().Single().ParameterType;
                context.SetPropertyValue(propertyName, newValue,
                    _ => ReadProperty(propertyName, propertyType),
                    (_, value) => WriteProperty(propertyName, value));

                invocation.ReturnValue = null;
            }
            else
            {
                invocation.ReturnValue = context.InvokeMethod(
                    invocation.Method.Name, invocation.Arguments, parameters =>
                    {
                        parameters.CopyTo(invocation.Arguments, 0);
                        invocation.Proceed();
                        return invocation.ReturnValue;
                    });
            }
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
    }
}