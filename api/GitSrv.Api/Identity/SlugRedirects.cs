using System.Data;
using Dapper;

namespace GitSrv.Api.Identity;

/// <summary>
/// Records slug rename history so stale URLs can 301 to the current location, and keeps a fresh
/// slug from silently shadowing an old one that still points elsewhere.
/// </summary>
public static class SlugRedirects
{
    public const string OrgScope = "org";
    public static string RepoScope(long orgId) => $"repo:{orgId}";

    /// <summary>Call inside the same transaction that performs the rename.</summary>
    public static async Task RecordRenameAsync(IDbConnection conn, IDbTransaction tx, string scope, string oldSlug, string newSlug)
    {
        // Drop any redirect that pointed at the slug we're now reusing as a live name.
        await conn.ExecuteAsync(
            "DELETE FROM slug_redirects WHERE scope = @scope AND old_slug = @newSlug",
            new { scope, newSlug }, tx);

        // Point the vacated slug (and anything that already pointed to it) at the new name.
        await conn.ExecuteAsync(
            "UPDATE slug_redirects SET new_slug = @newSlug WHERE scope = @scope AND new_slug = @oldSlug",
            new { scope, oldSlug, newSlug }, tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO slug_redirects (scope, old_slug, new_slug)
            VALUES (@scope, @oldSlug, @newSlug)
            ON CONFLICT (scope, old_slug) DO UPDATE SET new_slug = EXCLUDED.new_slug, created_at = now()
            """,
            new { scope, oldSlug, newSlug }, tx);
    }

    public static async Task<string?> ResolveAsync(IDbConnection conn, string scope, string oldSlug)
        => await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT new_slug FROM slug_redirects WHERE scope = @scope AND old_slug = @oldSlug",
            new { scope, oldSlug });
}
