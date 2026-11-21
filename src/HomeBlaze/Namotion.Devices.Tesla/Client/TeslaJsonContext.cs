using System.Text.Json.Serialization;
using Namotion.Devices.Tesla.Client.Dto;

namespace Namotion.Devices.Tesla.Client;

[JsonSourceGenerationOptions(
    Converters = [typeof(LenientInt32Converter), typeof(LenientInt64Converter), typeof(LenientDecimalConverter), typeof(LenientBooleanConverter)])]
[JsonSerializable(typeof(TeslaResponse<TeslaProductDto[]>))]
[JsonSerializable(typeof(TeslaResponse<TeslaVehicleDataDto>))]
[JsonSerializable(typeof(TeslaTokenResponseDto))]
internal sealed partial class TeslaJsonContext : JsonSerializerContext;
