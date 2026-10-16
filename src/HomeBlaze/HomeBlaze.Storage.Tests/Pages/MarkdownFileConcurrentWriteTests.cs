using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// Saves of two blocks of one page that overlap, as two viewers who change two subjects of one page at once.
/// </summary>
public class MarkdownFileConcurrentWriteTests : StorageTestBase
{
    [Fact]
    public async Task WhenTwoBlocksOfOnePageAreSavedAtOnce_ThenBothValuesAreInTheFileAndInMemory()
    {
        // Arrange
        WriteFile("Page.md", TwoBlocks("first", "second"));
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Page.md"]);
        var first = Assert.IsType<CallbackSubject>(page.Children["first"]);
        var second = Assert.IsType<CallbackSubject>(page.Children["second"]);
        first.Name = "first, saved";
        second.Name = "second, saved";
        using var blockingRead = new BlockingRead(first, nameof(CallbackSubject.Name));
        Context!.AddService<IReadInterceptor>(blockingRead);
        blockingRead.Arm();

        // Act: the first save stops while it reads the configuration of its block, and the second starts then.
        var firstSave = Task.Run(() => page.WriteConfigurationAsync(first, CancellationToken.None));
        await blockingRead.Reached.WaitAsync(DeadlockTimeout);
        var secondSave = page.WriteConfigurationAsync(second, CancellationToken.None);
        blockingRead.Release();
        var results = await Task.WhenAll(firstSave, secondSave).WaitAsync(DeadlockTimeout);

        // Assert
        Assert.Equal([true, true], results);
        var saved = File.ReadAllText(GetFullPath("Page.md"));
        Assert.Contains("first, saved", saved);
        Assert.Contains("second, saved", saved);
        Assert.Equal(("first, saved", "second, saved"), (first.Name, second.Name));
        Assert.Same(first, page.Children["first"]);
        Assert.Same(second, page.Children["second"]);
    }

    [Fact]
    public async Task WhenOtherBlockOfThePageChanges_ThenTheUnsavedValueOfAnUnchangedBlockIsKept()
    {
        // Arrange
        WriteFile("Page.md", TwoBlocks("first", "second"));
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Page.md"]);
        var first = Assert.IsType<CallbackSubject>(page.Children["first"]);
        var second = Assert.IsType<CallbackSubject>(page.Children["second"]);
        second.Name = "not saved yet";

        // Act
        WriteFile("Page.md", TwoBlocks("first, edited", "second"));
        await storage.ReconcileAsync(Named("Page.md"));

        // Assert
        Assert.Equal("first, edited", first.Name);
        Assert.Equal("not saved yet", second.Name);
        Assert.Same(second, page.Children["second"]);
    }

    [Fact]
    public async Task WhenConfigurationThatTheReloadOfASaveAppliesSavesThePage_ThenTheCallIsRefusedAndTheSaveCompletes()
    {
        // Arrange
        WriteFile("Page.md", $"# Page\n\n{CallbackSubject.SerializeBlock("valve", "first")}");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Page.md"]);
        var valve = Assert.IsType<CallbackSubject>(page.Children["valve"]);
        Task<bool>? refusedCall = null;
        CallbackSubject.RunOnNextApply(cancellationToken =>
        {
            refusedCall = page.WriteConfigurationAsync(valve, cancellationToken);
            return Task.CompletedTask;
        });
        valve.Name = "saved";

        // Act
        var isWritten = await page.WriteConfigurationAsync(valve, CancellationToken.None).WaitAsync(DeadlockTimeout);

        // Assert
        Assert.True(isWritten);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => refusedCall!);
        Assert.Equal(StorageWorker.CallFromWithinItemMessage, refusal.Message);
        Assert.Contains("saved", File.ReadAllText(GetFullPath("Page.md")));
    }

    private static string TwoBlocks(string firstName, string secondName) =>
        $"# Page\n\n{CallbackSubject.SerializeBlock("first", firstName)}\n{CallbackSubject.SerializeBlock("second", secondName)}";

    /// <summary>Blocks the thread of the next read of one property of one subject until it is released.</summary>
    private sealed class BlockingRead(IInterceptorSubject subject, string propertyName) : IReadInterceptor, IDisposable
    {
        private readonly ManualResetEventSlim _released = new();
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _isArmed;

        public Task Reached => _reached.Task;

        public void Arm() => Volatile.Write(ref _isArmed, 1);

        public void Release() => _released.Set();

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, subject) &&
                context.Property.Name == propertyName &&
                Interlocked.Exchange(ref _isArmed, 0) == 1)
            {
                _reached.TrySetResult();
                _released.Wait(DeadlockTimeout);
            }

            return next(ref context);
        }

        public void Dispose()
        {
            _released.Set();
            _released.Dispose();
        }
    }
}
