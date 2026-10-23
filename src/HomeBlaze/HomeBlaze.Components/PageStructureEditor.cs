using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// Changes the blocks of the page that is edited in place. A page cascades it while it is edited inline, and the widgets
/// show their structure actions only then. The members for blocks of text have defaults that do nothing, for an editor of a
/// page without them.
/// </summary>
public abstract class PageStructureEditor
{
    /// <summary>Gets what the block of the subject allows, all false when the subject has no block of its own.</summary>
    public abstract PageBlockActions GetActions(IInterceptorSubject subject);

    /// <summary>
    /// Gets the slots of the block of <paramref name="owner"/> to which a widget, text or an item can be added, grouped by
    /// <see cref="PageAddTarget.Kind"/> in the order of its values and in the order the type declares them within a kind.
    /// None when the subject has no block of its own.
    /// </summary>
    public abstract IReadOnlyList<PageAddTarget> GetAddTargets(IInterceptorSubject owner);

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

    /// <summary>
    /// Asks for a widget, adds it to <paramref name="target"/> of <paramref name="owner"/>, a slot of content or of content
    /// items, and selects it.
    /// </summary>
    public abstract Task AddWidgetAsync(IInterceptorSubject owner, PageAddTarget target);

    /// <summary>
    /// Asks for text, adds it to <paramref name="target"/> of <paramref name="owner"/>, a slot of content or of content items,
    /// and selects it. Does nothing by default.
    /// </summary>
    public virtual Task AddTextAsync(IInterceptorSubject owner, PageAddTarget target) => Task.CompletedTask;

    /// <summary>
    /// Asks for an item of <paramref name="target"/>, a slot of <see cref="PageAddTargetKind.ConfiguredItem"/>, appends it to
    /// the slot of <paramref name="owner"/> and selects it.
    /// </summary>
    public abstract Task AddListItemAsync(IInterceptorSubject owner, PageAddTarget target);

    /// <summary>Asks for the new text of the text block <paramref name="subject"/>, saves it and selects it. Does nothing by default.</summary>
    public virtual Task EditTextAsync(IInterceptorSubject subject) => Task.CompletedTask;

    /// <summary>
    /// Switches the page to an editor of its source and shows the lines of the block of <paramref name="subject"/> there, for
    /// content that cannot be edited inline. Does nothing by default.
    /// </summary>
    public virtual Task EditInSourceAsync(IInterceptorSubject subject) => Task.CompletedTask;

    /// <summary>Gets whether text can be inserted into <paramref name="content"/> before <paramref name="before"/>, or at its end. False by default.</summary>
    public virtual bool CanInsertText(IInterceptorSubject content, IInterceptorSubject? before) => false;

    /// <summary>
    /// Asks for text, inserts it into <paramref name="content"/> before <paramref name="before"/>, or at its end, and selects
    /// it. Does nothing by default.
    /// </summary>
    public virtual Task InsertTextAsync(IInterceptorSubject content, IInterceptorSubject? before) => Task.CompletedTask;
}
