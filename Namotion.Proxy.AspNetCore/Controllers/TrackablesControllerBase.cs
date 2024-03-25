using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc;

using Namotion.Proxy.Abstractions;
using Namotion.Proxy.Attributes;

namespace Namotion.Proxy.AspNetCore.Controllers;

public abstract class TrackablesControllerBase<TProxy> : ControllerBase
    where TProxy : class, IProxy
{
    private readonly IProxyContext _context;
    private readonly TProxy _proxy;

    // TODO: Inject IProxyContext<TProxy> so that multiple contexts are supported.

    protected TrackablesControllerBase(IProxyContext context, TProxy proxy)
    {
        _context = context;
        _proxy = proxy;
    }

    [HttpGet]
    public ActionResult<TProxy> GetVariables()
    {
        // TODO: correctly generate OpenAPI schema

        var jsonObject = new JsonObject();
        Populate(_proxy, _context.GetHandler<IProxyRegistry>(), jsonObject);
        return Ok(jsonObject);
    }

    //[HttpPost]
    //public ActionResult UpdateVariables(
    //    [FromBody] Dictionary<string, JsonElement> updates/*,
    //    [FromServices] IEnumerable<ITrackablePropertyValidator> propertyValidators*/)
    //{
    //    try
    //    {
    //        var resolvedUpdates = updates
    //            .Select(t =>
    //            {
    //                var variable = _context
    //                    .GetHandler<IProxyRegistry>()
    //                    .GetProperties()
    //                    .SingleOrDefault(v => v.Path.ToLowerInvariant() == t.Key.ToLowerInvariant());

    //                return new
    //                {
    //                    t.Key,
    //                    Variable = variable,
    //                    Value = variable != null ?
    //                        t.Value.Deserialize(variable.PropertyType) :
    //                        null
    //                };
    //            })
    //            .ToArray();

    //        // check only known variables
    //        if (resolvedUpdates.Any(u => u.Variable == null))
    //        {
    //            return BadRequest(new ProblemDetails
    //            {
    //                Detail = "Unknown variable paths."
    //            });
    //        }

    //        // check not read-only
    //        if (resolvedUpdates.Any(u => !u.Variable!.IsWriteable))
    //        {
    //            return BadRequest(new ProblemDetails
    //            {
    //                Detail = "Attempted to change read only variable."
    //            });
    //        }

    //        // run validators
    //        //var errors = new Dictionary<string, ValidationResult[]>();
    //        //foreach (var update in resolvedUpdates)
    //        //{
    //        //    var updateErrors = propertyValidators
    //        //        .SelectMany(v => v.Validate(update.Variable!, update.Value, _context))
    //        //        .ToArray();

    //        //    if (updateErrors.Any())
    //        //    {
    //        //        errors.Add(update.Key, updateErrors);
    //        //    }
    //        //}

    //        //if (errors.Any())
    //        //{
    //        //    return BadRequest(new ProblemDetails
    //        //    {
    //        //        Detail = "Variable updates not valid.",
    //        //        Extensions =
    //        //        {
    //        //            { "errors", errors.ToDictionary(e => e.Key, e => e.Value.Select(v => v.ErrorMessage)) }
    //        //        }
    //        //    });
    //        //}

    //        // write updates
    //        foreach (var update in resolvedUpdates)
    //        {
    //            update.Variable!.SetValue(update.Value);
    //        }

    //        return Ok();
    //    }
    //    catch (JsonException)
    //    {
    //        return BadRequest(new ProblemDetails
    //        {
    //            Detail = "Invalid variable value."
    //        });
    //    }
    //}

    ///// <summary>
    ///// Gets all leaf properties.
    ///// </summary>
    ///// <returns></returns>
    //[HttpGet("properties")]
    //public ActionResult GetProperties()
    //{
    //    var allTrackers = _context.AllTrackers;
    //    return Ok(_context
    //        .AllProperties
    //        .Where(p => !p.IsAttribute && allTrackers.Any(t => t.ParentProperty == p) == false));
    //}

    private static void Populate(IProxy proxy, IProxyRegistry register, JsonObject obj)
    {
        if (register.KnownProxies.TryGetValue(proxy, out var metadata))
        {
            foreach (var property in metadata.Properties.Where(p => p.Value.GetValue is not null))
            {
                var name = GetPropertyName(metadata, property.Key, property.Value);
                var value = property.Value.GetValue?.Invoke();
                if (value is IProxy childProxy)
                {
                    var child = new JsonObject();
                    Populate(childProxy, register, child);
                    obj[name] = child;
                }
                else if (value is IEnumerable<IProxy> collection)
                {
                    var children = new JsonArray();
                    foreach (var arrayProxyItem in collection)
                    {
                        var child = new JsonObject();
                        Populate(arrayProxyItem, register, child);
                        children.Add(child);
                    }
                    obj[name] = children;
                }
                else
                {
                    obj[name] = JsonValue.Create(value);
                }
            }
        }
    }

    private static string GetPropertyName(ProxyMetadata metadata, string name, ProxyProperty property)
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
}
