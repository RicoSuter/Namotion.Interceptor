namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>Unique names for the people and roles that tests add to the shared server.</summary>
public static class TestNames
{
    /// <summary>The prefix followed by eight random characters, so that tests never collide on a name.</summary>
    public static string Create(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 8)];
}
