using System.Collections.Concurrent;
using System.Reflection;
using Castle.DynamicProxy;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Dynamic;

public class DynamicSubjectFactory
{
    private static readonly ProxyGenerator ProxyGenerator = new();
    private static readonly ConcurrentDictionary<string, SubjectPropertyMetadata[]> PropertyCache = new();

    public static IInterceptorSubject CreateDynamicSubject(params Type[] interfaces)
    {
        return CreateSubject<DynamicSubject>(interfaces);
    }

    public static TSubject CreateSubject<TSubject>(params Type[] interfaces)
        where TSubject : IInterceptorSubject
    {
        return (TSubject)CreateSubject(typeof(TSubject), interfaces);
    }
    
    public static IInterceptorSubject CreateSubject(Type type, params Type[] interfaces)
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
                    property.GetCustomAttributesIncludingInterfaces(),
                    property.GetValue,
                    property.SetValue,
                    isIntercepted: true,
                    isDynamic: false))
                .ToArray();
        }, subject);

        subject.AddProperties(missingProperties);
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
                var propertyType = invocation.Method.GetParameters().Single().ParameterType;

                var newValue = invocation.Arguments[0];
                var currentValue = ReadProperty(propertyName, propertyType);

                // Runtime routing uses the same classifier as the lifecycle, so a write takes
                // the synchronized structural accessor exactly when the lifecycle would treat its
                // declared type as subject-bearing; the generator's compile-time routing stays
                // fail-closed because it cannot run this classifier.
                if (propertyType.CanContainSubjects())
                {
                    context.SetStructuralPropertyValue(propertyName, newValue, currentValue,
                        (_, value) => WriteProperty(propertyName, value));
                }
                else
                {
                    context.SetPropertyValue(propertyName, newValue, currentValue,
                        (_, value) => WriteProperty(propertyName, value));
                }

                invocation.ReturnValue = null;
            }
            else
            {
                invocation.ReturnValue = context.InvokeMethod(
                    // TODO: Should we really throw away subject here?
                    invocation.Method.Name, invocation.Arguments, (_, parameters) =>
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