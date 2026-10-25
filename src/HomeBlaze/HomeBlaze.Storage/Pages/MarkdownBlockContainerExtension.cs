using Markdig;
using Markdig.Renderers;

namespace HomeBlaze.Storage.Pages;

/// <summary>Adds the parser for blocks to a pipeline. It must be the last extension of the pipeline.</summary>
internal sealed class MarkdownBlockContainerExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        // First, because another parser for the same character moves the position of the line back
        // before its indentation when it declines the line.
        pipeline.BlockParsers.Insert(0, new MarkdownBlockContainerParser());
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        // The renderer of a block belongs to one rendering of one page, so the segment builder adds it.
    }
}
