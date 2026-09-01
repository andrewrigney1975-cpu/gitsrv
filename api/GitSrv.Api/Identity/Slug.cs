using System.Text.RegularExpressions;

namespace GitSrv.Api.Identity;

/// <summary>
/// Slug validation and normalisation for usernames, org, team and repo identifiers. Slugs are
/// stored already-normalised, so uniqueness is a plain UNIQUE constraint.
/// </summary>
public static partial class Slug
{
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9]|[-_](?![-_]))*[a-z0-9]$|^[a-z0-9]$")]
    private static partial Regex Pattern();

    public const int MaxLength = 40;

    // Names that would collide with top-level routes or have special meaning.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "admin", "api", "assets", "css", "js", "static", "public", "health", "login", "logout",
        "signin", "signup", "register", "settings", "help", "about", "new", "explore", "search",
        "notifications", "dashboard", "orgs", "organizations", "organisations", "teams", "users",
        "user", "repo", "repos", "repositories", "git", "gitsrv", "www", "mail", "ssh", "-", "_",
        "abuse", "security", "status", "billing", "pricing", "docs", "blog", "support",
    };

    public static bool IsValid(string candidate) =>
        !string.IsNullOrEmpty(candidate)
        && candidate.Length <= MaxLength
        && Pattern().IsMatch(candidate)
        && !Reserved.Contains(candidate);

    public static bool IsReserved(string candidate) => Reserved.Contains(candidate.ToLowerInvariant());

    /// <summary>Lowercase and trim; does not guarantee validity — call <see cref="IsValid"/> after.</summary>
    public static string Normalise(string input) => input.Trim().ToLowerInvariant();

    /// <summary>Best-effort slug from a display name, for pre-filling forms.</summary>
    public static string Suggest(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        var slug = SlugifyPattern().Replace(lower, "-").Trim('-', '_');
        if (slug.Length > MaxLength)
            slug = slug[..MaxLength].Trim('-', '_');
        return slug;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugifyPattern();
}
