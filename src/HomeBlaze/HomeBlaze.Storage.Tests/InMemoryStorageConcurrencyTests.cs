using System.Text;

namespace HomeBlaze.Storage.Tests;

public class InMemoryStorageConcurrencyTests : StorageTestBase
{
    [Fact]
    public async Task WhenSeparateInMemoryStoragesAreWrittenAtOnce_ThenEveryWriteSucceeds()
    {
        // Arrange
        const int storageCount = 8;
        const int writesPerStorage = 200;
        var storages = new List<FluentStorageContainer>();
        for (var index = 0; index < storageCount; index++)
        {
            storages.Add(await ConnectAsync(configure: storage => storage.StorageType = "inmemory"));
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act: each storage writes on its own thread of the pool, and all start at once.
        var writes = storages
            .Select(storage => Task.Run(async () =>
            {
                await start.Task;
                for (var index = 0; index < writesPerStorage; index++)
                {
                    await storage.WriteBlobAsync(
                        $"File{index}.md", Encoding.UTF8.GetBytes($"# File {index}"), CancellationToken.None);
                }
            }))
            .ToArray();
        start.SetResult();
        await Task.WhenAll(writes).WaitAsync(DeadlockTimeout);

        // Assert
        foreach (var storage in storages)
        {
            await using var stream = await storage.ReadBlobAsync($"File{writesPerStorage - 1}.md", CancellationToken.None);
            using var reader = new StreamReader(stream);
            Assert.Equal($"# File {writesPerStorage - 1}", await reader.ReadToEndAsync());
        }
    }
}
