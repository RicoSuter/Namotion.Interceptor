namespace HomeBlaze.Storage.Pages;

/// <summary>What the block of a subject allows on its page, all false for a subject without a block.</summary>
/// <param name="CanDelete">Whether the subject has a block that can be deleted.</param>
/// <param name="CanMoveUp">Whether the block can swap with the block before it.</param>
/// <param name="CanMoveDown">Whether the block can swap with the block after it.</param>
/// <param name="ContainedBlockCount">How many blocks are inside the block and go with it.</param>
public readonly record struct MarkdownBlockActions(bool CanDelete, bool CanMoveUp, bool CanMoveDown, int ContainedBlockCount);
