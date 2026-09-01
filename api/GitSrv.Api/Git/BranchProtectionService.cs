using System.Text.RegularExpressions;
using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Data;
using LibGit2Sharp;
using Repository = LibGit2Sharp.Repository;

namespace GitSrv.Api.Git;

public sealed record BranchProtection(long Id, string Pattern, bool RequirePullRequest, int RequiredApprovals,
    bool RequireStatusChecks, bool BlockForcePush, bool BlockDeletion, bool RequireLinearHistory, bool RestrictPush);

public sealed record RefUpdate(string Ref, string OldSha, string NewSha);

public sealed class BranchProtectionService(Db db, Authorizer authz, GitStorage storage)
{
    private const string Zero = "0000000000000000000000000000000000000000";

    public async Task<IReadOnlyList<BranchProtection>> ListAsync(long repoId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<BranchProtection>("""
            SELECT id, pattern, require_pull_request AS RequirePullRequest, required_approvals AS RequiredApprovals,
                   require_status_checks AS RequireStatusChecks, block_force_push AS BlockForcePush, block_deletion AS BlockDeletion,
                   require_linear_history AS RequireLinearHistory, restrict_push AS RestrictPush
            FROM branch_protections WHERE repo_id = @repoId ORDER BY pattern
            """, new { repoId })).ToList();
    }

    public async Task<long> UpsertAsync(long repoId, long? id, BranchProtection p, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.Pattern)) throw new ValidationException("A branch name or pattern is required.");
        await using var conn = await db.OpenAsync(ct);
        var args = new
        {
            repoId, id, p.Pattern, p.RequirePullRequest, p.RequiredApprovals, p.RequireStatusChecks,
            p.BlockForcePush, p.BlockDeletion, p.RequireLinearHistory, p.RestrictPush,
        };
        if (id is not null)
        {
            await conn.ExecuteAsync("""
                UPDATE branch_protections SET pattern = @Pattern, require_pull_request = @RequirePullRequest,
                    required_approvals = @RequiredApprovals, require_status_checks = @RequireStatusChecks,
                    block_force_push = @BlockForcePush, block_deletion = @BlockDeletion,
                    require_linear_history = @RequireLinearHistory, restrict_push = @RestrictPush
                WHERE id = @id AND repo_id = @repoId
                """, args);
            return id.Value;
        }
        try
        {
            return await conn.ExecuteScalarAsync<long>("""
                INSERT INTO branch_protections (repo_id, pattern, require_pull_request, required_approvals, require_status_checks,
                    block_force_push, block_deletion, require_linear_history, restrict_push)
                VALUES (@repoId, @Pattern, @RequirePullRequest, @RequiredApprovals, @RequireStatusChecks,
                    @BlockForcePush, @BlockDeletion, @RequireLinearHistory, @RestrictPush)
                RETURNING id
                """, args);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "23505")
        {
            throw new ValidationException("A rule for that pattern already exists.");
        }
    }

    public async Task DeleteAsync(long repoId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM branch_protections WHERE id = @id AND repo_id = @repoId", new { id, repoId });
    }

    public async Task<BranchProtection?> MatchAsync(long repoId, string branch, CancellationToken ct)
    {
        var rules = await ListAsync(repoId, ct);
        return rules.FirstOrDefault(r => MatchPattern(r.Pattern, branch));
    }

    private static bool MatchPattern(string pattern, string branch)
    {
        if (pattern == branch) return true;
        if (!pattern.Contains('*')) return false;
        var rx = "^" + Regex.Escape(pattern).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*") + "$";
        return Regex.IsMatch(branch, rx);
    }

    /// <summary>Called from the pre-receive hook. Returns null to allow, or a rejection reason.</summary>
    public async Task<string?> EvaluatePushAsync(long orgId, long repoId, long? pusherId, IReadOnlyList<RefUpdate> updates, CancellationToken ct)
    {
        var rules = await ListAsync(repoId, ct);
        if (rules.Count == 0) return null;

        using var repo = new Repository(storage.RepoPath(orgId, repoId));
        RepoPermission pusherPerm = pusherId is { } pid ? await authz.GetRepoPermissionAsync(pid, repoId, ct) : RepoPermission.None;

        foreach (var u in updates)
        {
            if (!u.Ref.StartsWith("refs/heads/")) continue;
            var branch = u.Ref["refs/heads/".Length..];
            var rule = rules.FirstOrDefault(r => MatchPattern(r.Pattern, branch));
            if (rule is null) continue;

            var creating = u.OldSha == Zero;
            var deleting = u.NewSha == Zero;

            if (deleting)
            {
                if (rule.BlockDeletion) return $"Branch '{branch}' is protected and cannot be deleted.";
                continue;
            }

            if (!creating && rule.RequirePullRequest)
                return $"Branch '{branch}' is protected — changes must go through a pull request.";

            if (rule.RestrictPush && pusherPerm < RepoPermission.Maintain)
                return $"Only maintainers may push to '{branch}'.";

            var newCommit = repo.Lookup<Commit>(u.NewSha);
            if (newCommit is null) return "Push contains an unknown commit.";

            if (!creating && rule.BlockForcePush)
            {
                var oldCommit = repo.Lookup<Commit>(u.OldSha);
                if (oldCommit is not null)
                {
                    var mb = repo.ObjectDatabase.FindMergeBase(oldCommit, newCommit);
                    if (mb?.Sha != oldCommit.Sha)
                        return $"Force pushes to '{branch}' are not allowed.";
                }
            }

            if (rule.RequireLinearHistory)
            {
                var offending = WalkNew(repo, u.OldSha, newCommit).FirstOrDefault(c => c.Parents.Count() > 1);
                if (offending is not null)
                    return $"Branch '{branch}' requires a linear history — merge commit {offending.Sha[..7]} is not allowed (rebase instead).";
            }
        }
        return null;
    }

    private static IEnumerable<Commit> WalkNew(Repository repo, string oldSha, Commit tip)
    {
        var filter = new CommitFilter { IncludeReachableFrom = tip };
        if (oldSha != Zero && repo.Lookup<Commit>(oldSha) is { } old)
            filter.ExcludeReachableFrom = old;
        return repo.Commits.QueryBy(filter).Take(200);
    }
}
