namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// What a storage tells the setup service about the place the new subject is created in.
/// </summary>
/// <param name="ExistingNames">The names that are already used there, with or without file extension.</param>
public record SubjectSetupRequest(IReadOnlyCollection<string> ExistingNames);
