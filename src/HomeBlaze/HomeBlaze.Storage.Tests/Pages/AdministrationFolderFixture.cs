namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// The administration folder of the documentation, loaded once for the tests of a class. The tests only read it.
/// </summary>
public sealed class AdministrationFolderFixture : IAsyncLifetime
{
    private DocumentationFolderFixture? _folder;

    public DocumentationFolderFixture Folder => _folder ?? throw new InvalidOperationException("The folder is not loaded yet.");

    public async Task InitializeAsync()
    {
        _folder = await DocumentationFolderFixture.LoadAsync("administration");
    }

    public Task DisposeAsync()
    {
        _folder?.Dispose();
        return Task.CompletedTask;
    }
}
