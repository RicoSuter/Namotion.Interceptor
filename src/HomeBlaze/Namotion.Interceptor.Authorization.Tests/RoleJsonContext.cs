using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Authorization.Tests;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AuthorizationRole))]
internal sealed partial class RoleJsonContext : JsonSerializerContext;
