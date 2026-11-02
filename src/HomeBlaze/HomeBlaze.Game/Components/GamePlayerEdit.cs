namespace HomeBlaze.Game.Components;

/// <summary>
/// What a player chose in the edit dialog.
/// </summary>
/// <param name="Name">The new name of its client, null to keep the name.</param>
/// <param name="Character">The person it looks like.</param>
/// <param name="Color">The shirt color as <c>#rrggbb</c>.</param>
public sealed record GamePlayerEdit(string? Name, string Character, string Color);
