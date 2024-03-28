using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Namotion.Proxy.Registry.Abstractions;
using Namotion.Proxy.Registry.Attributes;
using Namotion.Proxy.Validation;

namespace Namotion.Proxy.AspNetCore.Controllers;

public abstract class ProxyControllerBase<TProxy> : ControllerBase
    where TProxy : class, IProxy
{
    private readonly IProxyContext _context;
    private readonly TProxy _proxy;

    // TODO: Inject IProxyContext<TProxy> so that multiple contexts are supported.

    protected ProxyControllerBase(IProxyContext context, TProxy proxy)
    {
        _context = context;
        _proxy = proxy;
    }

    [HttpGet]
    public ActionResult<TProxy> GetVariables()
    {
        // TODO: correctly generate OpenAPI schema
        return Ok(CreateJsonObject(_proxy, _context.GetHandler<IProxyRegistry>()));
    }

    [HttpPost]
    public ActionResult UpdateVariables(
        [FromBody] Dictionary<string, JsonElement> updates,
        [FromServices] IEnumerable<ITrackablePropertyValidator> propertyValidators)
    {
        try
        {
            var resolvedUpdates = updates
                .Select(t =>
                {
                    (var proxy, var property) = GetPropertyMetadata(_proxy, t.Key.Split('.'));

                    return new
                    {
                        t.Key,
                        t.Value,
                        Proxy = proxy,
                        Property = property
                    };
                })
                .ToArray();

            // check only known variables
            if (resolvedUpdates.Any(u => u.Proxy == null))
            {
                return BadRequest(new ProblemDetails
                {
                    Detail = "Unknown property paths."
                });
            }

            // check not read-only
            if (resolvedUpdates.Any(u => u.Property.SetValue is null))
            {
                return BadRequest(new ProblemDetails
                {
                    Detail = "Attempted to change read only property."
                });
            }

            // run validators
            var errors = new Dictionary<string, ValidationResult[]>();
            foreach (var update in resolvedUpdates)
            {
                var updateErrors = propertyValidators
                    .SelectMany(v => v.Validate(update.Proxy!, update.Property.PropertyName, update.Value, _context))
                    .ToArray();

                if (updateErrors.Any())
                {
                    errors.Add(update.Key, updateErrors);
                }
            }

            if (errors.Any())
            {
                return BadRequest(new ProblemDetails
                {
                    Detail = "Property updates not valid.",
                    Extensions =
                    {
                        { "errors", errors.ToDictionary(e => e.Key, e => e.Value.Select(v => v.ErrorMessage)) }
                    }
                });
            }

            // write updates
            foreach (var update in resolvedUpdates)
            {
                update.Property.SetValue?.Invoke(update.Proxy, update.Value.Deserialize(update.Property.Info.PropertyType));
            }

            return Ok();
        }
        catch (JsonException)
        {
            return BadRequest(new ProblemDetails
            {
                Detail = "Invalid property value."
            });
        }
    }

    private (IProxy?, PropertyMetadata) GetPropertyMetadata(IProxy proxy, string[] segments)
    {
        var next = segments[0];
        if (segments.Length > 1)
        {
            if (next.Contains('['))
            {
                var segs = next.Split('[', ']');
                next = segs[0];
                var index = int.Parse(segs[1]);

                var collection = proxy.Properties[next].GetValue?.Invoke(proxy) as ICollection;
                var child = collection?.OfType<IProxy>().ElementAt(index);
                return child is not null ? GetPropertyMetadata(child, segments.Skip(1).ToArray()) : (null, default);
            }
            else
            {
                var child = proxy.Properties[next].GetValue?.Invoke(proxy) as IProxy;
                return child is not null ? GetPropertyMetadata(child, segments.Skip(1).ToArray()) : (null, default);
            }
        }
        else
        {
            return (proxy, proxy.Properties[next]);
        }
    }

    /// <summary>
    /// Gets all leaf properties.
    /// </summary>
    /// <returns></returns>
    [HttpGet("properties")]
    public ActionResult<ProxyDescription> GetProperties()
    {
        return Ok(CreateProxyDescription(_proxy, _context.GetHandler<IProxyRegistry>()));
    }

    private static JsonObject CreateJsonObject(IProxy proxy, IProxyRegistry register)
    {
        // TODO: apply JSON naming policy
        var obj = new JsonObject();
        if (register.KnownProxies.TryGetValue(proxy, out var metadata))
        {
            foreach (var property in metadata.Properties
                .Where(p => p.Value.GetValue is not null))
            {
                var name = GetPropertyName(metadata, property.Key, property.Value);
                var value = property.Value.GetValue?.Invoke();
                if (value is IProxy childProxy)
                {
                    obj[name] = CreateJsonObject(childProxy, register);
                }
                else if (value is ICollection collection && collection.OfType<IProxy>().Any())
                {
                    var children = new JsonArray();
                    foreach (var arrayProxyItem in collection.OfType<IProxy>())
                    {
                        children.Add(CreateJsonObject(arrayProxyItem, register));
                    }
                    obj[name] = children;
                }
                else
                {
                    obj[name] = JsonValue.Create(value);
                }
            }
        }
        return obj;
    }

    private static string GetPropertyName(ProxyMetadata metadata, string name, ProxyPropertyMetadata property)
    {
        var attribute = property.Attributes
            .OfType<PropertyAttributeAttribute>()
            .FirstOrDefault();

        if (attribute is not null)
        {
            return GetPropertyName(metadata,
                attribute.PropertyName,
                metadata.Properties[attribute.PropertyName]) + "@" + attribute.AttributeName;
        }

        return name; // TODO: apply JSON naming policy
    }

    public class ProxyDescription
    {
        public Dictionary<string, ProxyPropertyDescription> Properties { get; } = new Dictionary<string, ProxyPropertyDescription>();
    }

    public class ProxyPropertyDescription
    {
        public IReadOnlyDictionary<string, ProxyPropertyDescription>? Attributes { get; init; }

        public object? Value { get; internal set; }

        public ProxyDescription? Proxy { get; set; }

        public List<ProxyDescription>? Proxies { get; set; }
    }

    private static ProxyDescription CreateProxyDescription(IProxy proxy, IProxyRegistry register)
    {
        var description = new ProxyDescription();

        if (register.KnownProxies.TryGetValue(proxy, out var metadata))
        {
            foreach (var property in metadata.Properties
                .Where(p => p.Value.GetValue is not null &&
                            p.Value.Attributes.OfType<PropertyAttributeAttribute>().Any() == false))
            {
                var name = GetPropertyName(metadata, property.Key, property.Value);
                var value = property.Value.GetValue?.Invoke();

                description.Properties[name] = CreateDescription(register, metadata, property.Key, value);
            }
        }

        return description;
    }

    private static ProxyPropertyDescription CreateDescription(IProxyRegistry register, ProxyMetadata metadata, string propertyKey, object? value)
    {
        var description = new ProxyPropertyDescription
        {
            Attributes = metadata.Properties
                .Where(p => p.Value.GetValue is not null &&
                            p.Value.Attributes.OfType<PropertyAttributeAttribute>().Any(a => a.PropertyName == propertyKey))
                .ToDictionary(
                    p => p.Value.Attributes.OfType<PropertyAttributeAttribute>().Single().AttributeName,
                    p => CreateDescription(register, metadata, p.Key, p.Value.GetValue?.Invoke()))
        };

        if (value is IProxy childProxy)
        {
            description.Proxy = CreateProxyDescription(childProxy, register);
        }
        else if (value is ICollection collection && collection.OfType<IProxy>().Any())
        {
            var children = new List<ProxyDescription>();
            foreach (var arrayProxyItem in collection.OfType<IProxy>())
            {
                children.Add(CreateProxyDescription(arrayProxyItem, register));
            }
            description.Proxies = children;
        }
        else
        {
            description.Value = value;
        }

        return description;
    }
}
