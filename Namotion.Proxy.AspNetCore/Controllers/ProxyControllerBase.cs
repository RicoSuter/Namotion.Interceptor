using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using Namotion.Proxy.Registry;
using Namotion.Proxy.Registry.Abstractions;
using Namotion.Proxy.Registry.Attributes;
using Namotion.Proxy.Validation;

namespace Namotion.Proxy.AspNetCore.Controllers;

public abstract class ProxyControllerBase<TProxy> : ControllerBase
    where TProxy : IProxy
{
    private readonly IProxyContext _context;
    private readonly TProxy _proxy;

    // TODO: Inject IProxyContext<TProxy> so that multiple contexts are supported.

    protected ProxyControllerBase(TProxy proxy)
    {
        _proxy = proxy;
        _context = proxy.Context ?? throw new ArgumentNullException(nameof(proxy.Context));
    }

    [HttpGet]
    public ActionResult<TProxy> GetVariables()
    {
        // TODO: correctly generate OpenAPI schema

        return Ok(_context
            .GetHandler<IProxyRegistry>()
            .SerializeProxyToJson(_proxy));
    }

    [HttpPost]
    public ActionResult UpdateVariables(
        [FromBody] Dictionary<string, JsonElement> updates,
        [FromServices] IEnumerable<IProxyPropertyValidator> propertyValidators)
    {
        try
        {
            var resolvedUpdates = updates
                .Select(t =>
                {
                    (var proxy, var property) = _proxy.FindPropertyFromJsonPath(t.Key);
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

    /// <summary>
    /// Gets all leaf properties.
    /// </summary>
    /// <returns></returns>
    [HttpGet("properties")]
    public ActionResult<ProxyDescription> GetProperties()
    {
        return Ok(CreateProxyDescription(_proxy, _context.GetHandler<IProxyRegistry>()));
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
                var name = metadata.GetJsonPropertyName(property.Key, property.Value);
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
