using System.Text.RegularExpressions;

namespace GitSrv.Api.Git;

public sealed record IssueRef(int Number, bool Closes);

/// <summary>
/// Pulls structured references out of free text (issue bodies, comments, commit messages):
/// <c>#123</c> issue/PR references, <c>closes #123</c> / <c>fixes #123</c> closing keywords, and
/// <c>@username</c> mentions.
/// </summary>
public static partial class TextRefs
{
    [GeneratedRegex(@"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingKeyword();

    [GeneratedRegex(@"(?<![\w/])#(\d+)\b")]
    private static partial Regex IssueHash();

    [GeneratedRegex(@"(?<![\w@])@([a-z0-9](?:[a-z0-9]|[-_](?![-_])){0,38}[a-z0-9]|[a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Mention();

    public static IReadOnlyList<IssueRef> IssueRefs(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var closing = ClosingKeyword().Matches(text).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
        var all = IssueHash().Matches(text).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
        all.UnionWith(closing);
        return all.Select(n => new IssueRef(n, closing.Contains(n))).ToList();
    }

    public static IReadOnlyList<string> Mentions(string text)
        => string.IsNullOrEmpty(text) ? []
            : Mention().Matches(text).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();
}
