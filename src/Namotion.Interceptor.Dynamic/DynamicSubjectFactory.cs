using System.Collections.Concurrent;
using System.Reflection;
using Castle.DynamicProxy;
using Namotion.Interceptor.Interceptors;

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

                // The runtime mirror of the generator's fail-closed routing: only a declared type
                // that provably cannot hold a subject takes the scalar route, everything else goes
                // through the synchronized structural accessor so a subject-bearing dynamic write
                // gets the same pre-chain gate ordering as a generated structural write.
                if (RequiresStructuralWrite(propertyType))
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

        private static readonly ConcurrentDictionary<Type, bool> StructuralRouteCache = new();

        private static bool RequiresStructuralWrite(Type type)
        {
            return StructuralRouteCache.GetOrAdd(type, static t => !IsProvablySubjectFree(t));
        }

        /// <summary>
        /// The same scalar allowlist the generator's PropertyWriteRouting uses: primitives, enums,
        /// string, decimal, DateTime, DateTimeOffset, TimeSpan, Guid, and Nullable of any of
        /// these. Everything else fails closed onto the structural route, which costs one gate
        /// entry on an uncommon property, while a false scalar answer would silently skip the
        /// pre-chain synchronization on exactly the path it exists for.
        /// </summary>
        private static bool IsProvablySubjectFree(Type type)
        {
            if (type.IsPrimitive || type.IsEnum)
            {
                return true;
            }

            if (type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) ||
                type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid))
            {
                return true;
            }

            var underlyingType = Nullable.GetUnderlyingType(type);
            return underlyingType is not null && IsProvablySubjectFree(underlyingType);
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