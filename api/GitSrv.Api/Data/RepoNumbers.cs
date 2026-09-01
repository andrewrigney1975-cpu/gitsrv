using System.Data;
using Dapper;

namespace GitSrv.Api.Data;

/// <summary>Issues and pull requests draw from one per-repo number sequence (table repo_number_seq).</summary>
public static class RepoNumbers
{
    public static async Task<int> NextAsync(IDbConnection conn, IDbTransaction tx, long repoId)
    {
        await conn.ExecuteAsync(
            "INSERT INTO repo_number_seq (repo_id, last_number) VALUES (@repoId, 0) ON CONFLICT DO NOTHING",
            new { repoId }, tx);
        return await conn.ExecuteScalarAsync<int>(
            "UPDATE repo_number_seq SET last_number = last_number + 1 WHERE repo_id = @repoId RETURNING last_number",
            new { repoId }, tx);
    }
}
