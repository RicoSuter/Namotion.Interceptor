using System.Collections.Immutable;
using HomeBlaze.Game.View;

namespace HomeBlaze.Game.People;

/// <summary>
/// The people a player can look like; each name is a model under <c>wwwroot/models/people/</c>.
/// </summary>
internal static class GamePeople
{
    public static readonly ImmutableArray<string> Names =
        ["male-casual", "male-shirt", "male-long-sleeve", "female-casual", "female-alternative", "female-tank-top"];

    /// <summary>
    /// Gets the person a client looks like until its player chooses one; stays the same across sessions.
    /// </summary>
    public static string GetDefault(string clientId) =>
        // A suffix decorrelates the person from the default color, which hashes the bare id.
        Names[(int)(GameColors.Hash(clientId + ":person") % (uint)Names.Length)];

    /// <summary>
    /// Gets the stored person when it is one of <see cref="Names"/>, else the default of the client.
    /// </summary>
    public static string Resolve(string? stored, string clientId) =>
        stored is not null && Names.Contains(stored) ? stored : GetDefault(clientId);
}
