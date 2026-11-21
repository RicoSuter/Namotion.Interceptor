using CarServer;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The active route of a Tesla vehicle. All properties are null while no route is active.
/// <see cref="TeslaDriveState"/> applies the data after its own timestamp check.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaNavigation : ITitleProvider, IIconProvider
{
    public string Title => "Navigation";

    public string IconName => "Navigation";

    [State(Position = 1)] public partial string? Destination { get; internal set; }
    [State(Position = 2)] public partial GeoCoordinate? DestinationLocation { get; internal set; }
    [State(Position = 3)] public partial TimeSpan? TimeToArrival { get; internal set; }
    [State(Unit = StateUnit.Kilometer, Position = 4)] public partial decimal? DistanceToArrival { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 5)] public partial decimal? EnergyAtArrival { get; internal set; }

    /// <summary>The delay that traffic adds to the time to arrival.</summary>
    [State(Position = 6)] public partial TimeSpan? TrafficDelay { get; internal set; }

    public TeslaNavigation()
    {
        Destination = null;
        DestinationLocation = null;
        TimeToArrival = null;
        DistanceToArrival = null;
        EnergyAtArrival = null;
        TrafficDelay = null;
    }

    internal void UpdateFromJson(TeslaDriveStateDto dto)
    {
        var destination = TeslaValues.Text(dto.ActiveRouteDestination);
        if (destination is null)
        {
            Clear();
            return;
        }

        Destination = destination;
        DestinationLocation = TeslaValues.Coordinate(dto.ActiveRouteLatitude, dto.ActiveRouteLongitude);
        TimeToArrival = TeslaValues.Minutes(dto.ActiveRouteMinutesToArrival);
        DistanceToArrival = TeslaValues.MilesToKilometers(dto.ActiveRouteMilesToArrival);
        EnergyAtArrival = TeslaValues.Percent(dto.ActiveRouteEnergyAtArrival);
        TrafficDelay = TeslaValues.Minutes(dto.ActiveRouteTrafficMinutesDelay);
    }

    internal void UpdateFromProtobuf(DriveState proto)
    {
        if (proto.OptionalActiveRouteDestinationCase != DriveState.OptionalActiveRouteDestinationOneofCase.None)
        {
            var destination = TeslaValues.Text(proto.ActiveRouteDestination);
            if (destination is null)
            {
                Clear();
                return;
            }

            Destination = destination;
        }

        if (proto.OptionalActiveRouteMinutesToArrivalCase != DriveState.OptionalActiveRouteMinutesToArrivalOneofCase.None) TimeToArrival = TeslaValues.Minutes(TeslaValues.Decimal(proto.ActiveRouteMinutesToArrival));
        if (proto.OptionalActiveRouteMilesToArrivalCase != DriveState.OptionalActiveRouteMilesToArrivalOneofCase.None) DistanceToArrival = TeslaValues.MilesToKilometers(TeslaValues.Decimal(proto.ActiveRouteMilesToArrival));
        if (proto.OptionalActiveRouteEnergyAtArrivalCase != DriveState.OptionalActiveRouteEnergyAtArrivalOneofCase.None) EnergyAtArrival = TeslaValues.Percent(TeslaValues.Decimal(proto.ActiveRouteEnergyAtArrival));
        if (proto.OptionalActiveRouteTrafficMinutesDelayCase != DriveState.OptionalActiveRouteTrafficMinutesDelayOneofCase.None) TrafficDelay = TeslaValues.Minutes(TeslaValues.Decimal(proto.ActiveRouteTrafficMinutesDelay));
        if (proto.ActiveRouteCoordinates is { } coordinates) DestinationLocation = TeslaValues.Coordinate(TeslaValues.Decimal(coordinates.Latitude), TeslaValues.Decimal(coordinates.Longitude));
    }

    private void Clear()
    {
        Destination = null;
        DestinationLocation = null;
        TimeToArrival = null;
        DistanceToArrival = null;
        EnergyAtArrival = null;
        TrafficDelay = null;
    }
}
