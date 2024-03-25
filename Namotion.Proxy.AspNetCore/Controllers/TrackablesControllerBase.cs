using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualBasic;
using Namotion.Proxy.Abstractions;
using Namotion.Proxy.Attributes;

namespace Namotion.Proxy.AspNetCore.Controllers;

public abstract class TrackablesControllerBase<TProxy> : ControllerBase
    where TProxy : class, IProxy
{
    private readonly static JsonSerializerOptions _options;

    private readonly IProxyContext _context;
    private readonly TProxy _proxy;

    // TODO: Inject IProxyContext<TProxy> so that multiple contexts are supported.
    protected TrackablesControllerBase(IProxyContext context, TProxy proxy)
    {
        _context = context;
        _proxy = proxy;
    }

    static TrackablesControllerBase()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        void RenameAttributeProperties(JsonTypeInfo typeInfo)
        {
            foreach (JsonPropertyInfo propertyInfo in typeInfo.Properties)
            {
                var variableAttribute = propertyInfo.AttributeProvider?
                    .GetCustomAttributes(true)
                    .OfType<PropertyAttributeAttribute>()
                    .FirstOrDefault();

                if (variableAttribute != null)
                {
                    propertyInfo.Name =
                        options.PropertyNamingPolicy.ConvertName(variableAttribute.PropertyName) + "@" +
                        options.PropertyNamingPolicy.ConvertName(variableAttribute.AttributeName);
                }
            }
        }

        options.Converters.Add(new JsonStringEnumConverter());
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { RenameAttributeProperties }
        };

        _options = options;
    }

    [HttpGet]
    public ActionResult<TProxy> GetVariables()
    {
        var jsonObject = new JsonObject();
        Populate(_proxy, _context.GetHandler<IProxyRegistry>(), jsonObject);

        //var json = JsonSerializer.SerializeToElement(_proxy, _options);
        return Ok(jsonObject);
    }

    public static void Populate(IProxy proxy, IProxyRegistry register, JsonObject obj)
    {
        if (register.KnownProxies.TryGetValue(proxy, out var metadata))
        {
            foreach (var property in metadata.Properties.Where(p => p.Value.GetValue is not null))
            {
                var key = property.Key;
               
                var attribute = property.Value.Attributes
                    .OfType<PropertyAttributeAttribute>()
                    .FirstOrDefault();

                if (attribute is not null)
                {
                    key = attribute.PropertyName + "@" + attribute.AttributeName;
                }

                var value = property.Value.GetValue?.Invoke();
                if (value is IProxy childProxy)
                {
                    var child = new JsonObject();
                    Populate(childProxy, register, child);
                    obj[key] = child;
                }
                else if (value is IEnumerable<IProxy> childProxies)
                {
                    var children = new JsonArray();
                    foreach (var childProxy2 in childProxies)
                    {
                        var child = new JsonObject();
                        Populate(childProxy2, register, child);
                        children.Add(child);
                    }
                    obj[key] = children;
                }
                else
                {
                    obj[key] = JsonValue.Create(value);
                }
            }
        }
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
}
