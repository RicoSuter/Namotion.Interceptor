using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// Changes the blocks of the page that is edited in place. A page cascades it while it is edited inline, and the
/// widgets show their structure actions only then.
/// </summary>
public interface IPageStructureEditor
{
    /// <summary>Gets what the block of the subject allows, all false when the subject has no block of its own.</summary>
    PageBlockActions GetActions(IInterceptorSubject subject);

    /// <summary>Gets the slots of the block of <paramref name="owner"/> that hold lists of blocks.</summary>
    IReadOnlyList<PageListSlot> GetListSlots(IInterceptorSubject owner);

    /// <summary>Gets the name of the block of the subject on the page, or null without one.</summary>
    string? GetBlockName(IInterceptorSubject subject);

    /// <summary>Gets whether new blocks can be inserted into <paramref name="content"/>, the page or a fragment of it.</summary>
    bool CanInsertInto(IInterceptorSubject content);

    /// <summary>Asks for confirmation and deletes the block of the subject.</summary>
    Task DeleteAsync(IInterceptorSubject subject);

    /// <summary>Swaps the block of the subject with the block before it (-1) or after it (1).</summary>
    Task MoveAsync(IInterceptorSubject subject, int direction);

    /// <summary>Asks for a widget and inserts it into <paramref name="content"/> before <paramref name="before"/>, or at its end.</summary>
    Task InsertWidgetAsync(IInterceptorSubject content, IInterceptorSubject? before);

    /// <summary>Asks for the item when the slot has a type and appends it to the slot of <paramref name="owner"/>.</summary>
    Task AddListItemAsync(IInterceptorSubject owner, PageListSlot slot);
}
