using Dapper;
using GitSrv.Api.Data;

namespace GitSrv.Api.Collab;

public sealed record ActivityItem(long Id, string? ActorUsername, string? OrgSlug, string? RepoSlug,
    string Kind, int? RefNumber, string Summary, DateTime CreatedAt);

public sealed class ActivityService(Db db)
{
    public async Task RecordAsync(long? actorId, long? orgId, long? repoId, string kind, int? refNumber, string summary, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        // Fill org from repo when only repo was given.
        await conn.ExecuteAsync("""
            INSERT INTO activity (actor_id, org_id, repo_id, kind, ref_number, summary)
            VALUES (@actorId, COALESCE(@orgId, (SELECT org_id FROM repositories WHERE id = @repoId)), @repoId, @kind, @refNumber, @summary)
            """, new { actorId, orgId, repoId, kind, refNumber, summary });
    }

    private const string Select = """
        SELECT a.id, u.username AS ActorUsername, o.slug AS OrgSlug, r.slug AS RepoSlug,
               a.kind, a.ref_number AS RefNumber, a.summary, a.created_at AS CreatedAt
        FROM activity a
        LEFT JOIN users u ON u.id = a.actor_id
        LEFT JOIN repositories r ON r.id = a.repo_id
        LEFT JOIN organisations o ON o.id = a.org_id
        """;

    public async Task<IReadOnlyList<ActivityItem>> RepoFeedAsync(long repoId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<ActivityItem>(Select + " WHERE a.repo_id = @repoId ORDER BY a.created_at DESC LIMIT @limit", new { repoId, limit })).ToList();
    }

    public async Task<IReadOnlyList<ActivityItem>> OrgFeedAsync(long orgId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<ActivityItem>(Select + " WHERE a.org_id = @orgId ORDER BY a.created_at DESC LIMIT @limit", new { orgId, limit })).ToList();
    }

    /// <summary>Activity across every org the user belongs to.</summary>
    public async Task<IReadOnlyList<ActivityItem>> UserFeedAsync(long userId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<ActivityItem>(Select + """
             WHERE a.org_id IN (SELECT org_id FROM org_members WHERE user_id = @userId)
             ORDER BY a.created_at DESC LIMIT @limit
            """, new { userId, limit })).ToList();
    }
}
