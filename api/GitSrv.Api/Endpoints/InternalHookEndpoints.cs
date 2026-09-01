using Dapper;
using GitSrv.Api.Data;
using GitSrv.Api.Git;

namespace GitSrv.Api.Endpoints;

/// <summary>
/// Called by the bare repos' pre-receive / post-receive hook scripts (never nginx, never the
/// internet). Line-oriented plain-text bodies so the POSIX shell hooks need no JSON tooling.
/// </summary>
public static class InternalHookEndpoints
{
    private static IReadOnlyList<RefUpdate> Parse(string body) =>
        body.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', 3))
            .Where(p => p.Length == 3)
            .Select(p => new RefUpdate(p[2].Trim(), p[0].Trim(), p[1].Trim()))
            .ToList();

    public static void MapInternalHooks(this IEndpointRouteBuilder app, string internalToken)
    {
        var g = app.MapGroup("/internal/hooks");
        g.AddEndpointFilter(async (ctx, next) =>
            string.IsNullOrEmpty(internalToken) || ctx.HttpContext.Request.Headers["X-Internal-Token"] != internalToken
                ? Results.StatusCode(403) : await next(ctx));

        g.MapPost("/pre-receive", async (long repoId, long? pusherId, HttpContext ctx, Db db,
            BranchProtectionService protections, CancellationToken ct) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var updates = Parse(await reader.ReadToEndAsync(ct));
            if (updates.Count == 0) return Results.Text("allow");

            await using var conn = await db.OpenAsync(ct);
            var orgId = await conn.QuerySingleOrDefaultAsync<long?>("SELECT org_id FROM repositories WHERE id = @repoId", new { repoId });
            if (orgId is null) return Results.Text("allow");

            var reason = await protections.EvaluatePushAsync(orgId.Value, repoId, pusherId, updates, ct);
            return reason is null ? Results.Text("allow") : Results.Text($"deny\nGitSrv: {reason}");
        });

        g.MapPost("/post-receive", async (long repoId, long? pusherId, HttpContext ctx, Db db, GitStorage storage,
            PullRequestService prs, WebhookService hooks, Collab.ActivityService activity,
            Actions.ActionsService actions, IConfiguration cfg, CancellationToken ct) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var updates = Parse(await reader.ReadToEndAsync(ct));

            await using var conn = await db.OpenAsync(ct);
            var meta = await conn.QuerySingleOrDefaultAsync<PushMeta>(
                "SELECT r.org_id AS OrgId, o.slug AS OrgSlug, r.slug AS RepoSlug FROM repositories r JOIN organisations o ON o.id = r.org_id WHERE r.id = @repoId",
                new { repoId });
            if (meta is null) return Results.NoContent();

            var pusher = pusherId is null ? null : await conn.ExecuteScalarAsync<string>("SELECT username FROM users WHERE id = @pusherId", new { pusherId });

            foreach (var u in updates.Where(u => u.Ref.StartsWith("refs/heads/")))
            {
                var branch = u.Ref["refs/heads/".Length..];
                var verb = u.OldSha.All(c => c == '0') ? "created" : u.NewSha.All(c => c == '0') ? "deleted" : "pushed to";
                await activity.RecordAsync(pusherId, meta.OrgId, repoId, "push", null,
                    $"{pusher ?? "someone"} {verb} {branch}", ct);
            }

            await prs.SyncAfterPushAsync(repoId, storage.RepoPath(meta.OrgId, repoId), ct);

            var publicBaseUrl = cfg["App:PublicBaseUrl"] ?? "http://localhost:8080";
            foreach (var u in updates.Where(u => u.Ref.StartsWith("refs/heads/") && !u.NewSha.All(c => c == '0')))
            {
                await actions.DispatchAsync(meta.OrgId, meta.OrgSlug, meta.RepoSlug, repoId, publicBaseUrl,
                    "push", u.Ref, u.NewSha, null, pusherId, ct);

                var branch = u.Ref["refs/heads/".Length..];
                var openPrs = await conn.QueryAsync<PrRef>(
                    "SELECT number, head_sha AS HeadSha FROM pull_requests WHERE repo_id = @repoId AND head_branch = @branch AND state = 'open'",
                    new { repoId, branch });
                foreach (var pr in openPrs)
                    await actions.DispatchAsync(meta.OrgId, meta.OrgSlug, meta.RepoSlug, repoId, publicBaseUrl,
                        "pull_request", u.Ref, pr.HeadSha, pr.Number, pusherId, ct);
            }

            await hooks.DeliverAsync(repoId, "push", new
            {
                repository = new { meta.OrgSlug, meta.RepoSlug },
                pusher,
                updates = updates.Select(u => new { u.Ref, before = u.OldSha, after = u.NewSha }),
            }, ct);

            return Results.NoContent();
        });
    }

    private sealed record PushMeta(long OrgId, string OrgSlug, string RepoSlug);
    private sealed record PrRef(int Number, string HeadSha);
}
