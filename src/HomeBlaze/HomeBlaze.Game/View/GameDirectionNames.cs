using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game.View;

internal static class GameDirectionNames
{
    public static string GetShortName(this GameDirection direction) => direction switch
    {
        GameDirection.North => "N",
        GameDirection.NorthEast => "NE",
        GameDirection.East => "E",
        GameDirection.SouthEast => "SE",
        GameDirection.South => "S",
        GameDirection.SouthWest => "SW",
        GameDirection.West => "W",
        _ => "NW"
    };
}
