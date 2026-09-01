using System.Text.RegularExpressions;
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

    /// <summary>
    /// Renders comment/issue Markdown and linkifies <c>#123</c> and <c>@user</c> against the given
    /// repo. Applied to text nodes only (never inside tags/attributes), so it can't inject markup.
    /// </summary>
    public static string ToCommentHtml(string markdown, string orgSlug, string repoSlug)
    {
        var html = Markdown.ToHtml(markdown ?? "", Pipeline);
        return LinkifyOutsideTags(html, orgSlug, repoSlug);
    }

    private static readonly Regex TagSplit = new("(<[^>]+>)", RegexOptions.Compiled);
    private static readonly Regex IssueHash = new(@"(?<![\w/&])#(\d+)\b", RegexOptions.Compiled);
    private static readonly Regex Mention = new(@"(?<![\w@/])@([A-Za-z0-9][-_A-Za-z0-9]{0,38})\b", RegexOptions.Compiled);

    private static string LinkifyOutsideTags(string html, string org, string repo)
    {
        var parts = TagSplit.Split(html);
        bool inCode = false;
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.StartsWith('<'))
            {
                if (p.StartsWith("<code", StringComparison.OrdinalIgnoreCase) || p.StartsWith("<pre", StringComparison.OrdinalIgnoreCase)) inCode = true;
                else if (p.StartsWith("</code", StringComparison.OrdinalIgnoreCase) || p.StartsWith("</pre", StringComparison.OrdinalIgnoreCase)) inCode = false;
                continue;
            }
            if (inCode || p.Length == 0) continue;
            p = IssueHash.Replace(p, m => $"<a href=\"#/o/{org}/{repo}/issues/{m.Groups[1].Value}\">#{m.Groups[1].Value}</a>");
            p = Mention.Replace(p, m => $"<a href=\"#/u/{m.Groups[1].Value}\">@{m.Groups[1].Value}</a>");
            parts[i] = p;
        }
        return string.Concat(parts);
    }

    public static readonly string[] ReadmeCandidates =
        ["README.md", "readme.md", "README.markdown", "README", "readme", "README.txt"];
}
