namespace HomeBlaze.Game.Clients;

/// <summary>
/// A browser client of the installation as the game sees it.
/// </summary>
internal readonly record struct GameClient(string Id, string Title, bool IsNamed, bool IsConnected);
