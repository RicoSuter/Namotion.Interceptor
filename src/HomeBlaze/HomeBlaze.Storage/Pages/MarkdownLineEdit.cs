namespace HomeBlaze.Storage.Pages;

/// <summary>What one change of the page text did to its lines.</summary>
/// <param name="Line">The 1-based line where the change starts: the first removed line, or the line of the first inserted line.</param>
/// <param name="RemovedLineCount">How many lines were removed from <paramref name="Line"/> on.</param>
/// <param name="InsertedLineCount">How many lines were inserted at <paramref name="Line"/>.</param>
public readonly record struct MarkdownLineEdit(int Line, int RemovedLineCount, int InsertedLineCount);

/// <summary>Where a new slot block goes and how it is written.</summary>
/// <param name="Line">The 1-based line before which the block is inserted. A line after the last line appends it.</param>
/// <param name="HasClosingLine">Whether the block and the blocks in it get a closing line.</param>
/// <param name="Indentation">The white space before the head line of the block around it, which every line of the new block gets.</param>
/// <param name="LineEnding">The line ending of the page at this place.</param>
public readonly record struct MarkdownBlockPlacement(int Line, bool HasClosingLine, string Indentation, string LineEnding);
