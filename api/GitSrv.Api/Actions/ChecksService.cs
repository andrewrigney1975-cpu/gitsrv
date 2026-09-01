using Dapper;
using GitSrv.Api.Data;

namespace GitSrv.Api.Actions;

public sealed record CommitStatus(string Context, string State, string Description, string TargetUrl, DateTime UpdatedAt);

public sealed class ChecksService(Db db)
{
    public async Task SetAsync(long repoId, string sha, string context, string state, string description, string targetUrl, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO commit_statuses (repo_id, sha, context, state, description, target_url)
            VALUES (@repoId, @sha, @context, @state, @description, @targetUrl)
            ON CONFLICT (repo_id, sha, context)
            DO UPDATE SET state = EXCLUDED.state, description = EXCLUDED.description, target_url = EXCLUDED.target_url, updated_at = now()
            """, new { repoId, sha, context, state, description = description ?? "", targetUrl = targetUrl ?? "" });
    }

    public async Task<IReadOnlyList<CommitStatus>> ForShaAsync(long repoId, string sha, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<CommitStatus>("""
            SELECT context, state, description, target_url AS TargetUrl, updated_at AS UpdatedAt
            FROM commit_statuses WHERE repo_id = @repoId AND sha = @sha ORDER BY context
            """, new { repoId, sha })).ToList();
    }

    /// <summary>All commit statuses for the sha are success (and at least one exists).</summary>
    public async Task<bool> AllGreenAsync(long repoId, string sha, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM commit_statuses WHERE repo_id = @repoId AND sha = @sha", new { repoId, sha });
        var green = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM commit_statuses WHERE repo_id = @repoId AND sha = @sha AND state = 'success'", new { repoId, sha });
        return total > 0 && total == green;
    }
}
