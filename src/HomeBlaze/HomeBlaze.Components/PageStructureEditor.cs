using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// Changes the blocks of the page that is edited in place. A page cascades it while it is edited inline, and the widgets
/// show their structure actions only then. A member added later has a default that does nothing, so that an editor of
/// another kind of page keeps working.
/// </summary>
public abstract class PageStructureEditor
{
    /// <summary>Gets what the block of the subject allows, all false when the subject has no block of its own.</summary>
    public abstract PageBlockActions GetActions(IInterceptorSubject subject);

    /// <summary>Gets the slots of the block of <paramref name="owner"/> that hold lists of blocks.</summary>
    public abstract IReadOnlyList<PageListSlot> GetListSlots(IInterceptorSubject owner);

    /// <summary>Gets the name of the block of the subject on the page, or null without one.</summary>
    public abstract string? GetBlockName(IInterceptorSubject subject);

    /// <summary>Gets whether new blocks can be inserted into <paramref name="content"/>, the page or a fragment of it.</summary>
    public abstract bool CanInsertInto(IInterceptorSubject content);

    /// <summary>Asks for confirmation and deletes the block of the subject.</summary>
    public abstract Task DeleteAsync(IInterceptorSubject subject);

    /// <summary>Moves the block of the subject one place up (-1) or down (1) among the content around it, and keeps it selected.</summary>
    public abstract Task MoveAsync(IInterceptorSubject subject, int direction);

    /// <summary>
    /// Asks for a widget, inserts it into <paramref name="content"/> before <paramref name="before"/>, or at its end, and
    /// selects it.
    /// </summary>
    public abstract Task InsertWidgetAsync(IInterceptorSubject content, IInterceptorSubject? before);

    /// <summary>Asks for the item when the slot has a type, appends it to the slot of <paramref name="owner"/> and selects it.</summary>
    public abstract Task AddListItemAsync(IInterceptorSubject owner, PageListSlot slot);

    /// <summary>Asks for the new text of the text block <paramref name="subject"/>, saves it and selects it. Does nothing by default.</summary>
    public virtual Task EditTextAsync(IInterceptorSubject subject) => Task.CompletedTask;
}
