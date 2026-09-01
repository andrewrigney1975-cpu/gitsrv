using Markdig;

namespace GitSrv.Api.Git;

/// <summary>
/// Server-side Markdown → HTML for READMEs and (later) comments. Raw/inline HTML in the source is
/// escaped, not passed through, so the output is safe to inject with innerHTML. Auto-linking and
/// GitHub-flavoured tables/task-lists are on.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .UseAutoLinks()
        .Build();

    public static string ToHtml(string markdown) => Markdown.ToHtml(markdown ?? "", Pipeline);

    public static readonly string[] ReadmeCandidates =
        ["README.md", "readme.md", "README.markdown", "README", "readme", "README.txt"];
}
