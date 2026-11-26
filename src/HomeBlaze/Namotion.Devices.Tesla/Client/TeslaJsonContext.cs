using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Tesla.Client.Dto;

namespace Namotion.Devices.Tesla.Client;

[JsonSourceGenerationOptions(
    Converters = [typeof(LenientInt32Converter), typeof(LenientInt64Converter), typeof(LenientDecimalConverter), typeof(LenientBooleanConverter)])]
[JsonSerializable(typeof(TeslaResponse<TeslaProductDto[]>))]
[JsonSerializable(typeof(TeslaResponse<TeslaVehicleDataDto>))]
[JsonSerializable(typeof(TeslaTokenResponseDto))]
internal sealed partial class TeslaJsonContext : JsonSerializerContext
{
    /// <summary>Creates a context that logs a value of an unexpected type as a warning. The converters of <c>Default</c> log nothing.</summary>
    public static TeslaJsonContext Create(ILogger logger) => new(new JsonSerializerOptions
    {
        Converters =
        {
            new LenientInt32Converter { Logger = logger },
            new LenientInt64Converter { Logger = logger },
            new LenientDecimalConverter { Logger = logger },
            new LenientBooleanConverter { Logger = logger }
        }
    });
}
