using Dapper;
using GitSrv.Api.Data;
using GitSrv.Api.Git;

namespace GitSrv.Api.Endpoints;

/// <summary>
/// Endpoints the <c>ssh</c> container calls (never nginx, never the internet). The API publishes no
/// host port, so these are only reachable on the compose network; a shared secret header is an
/// extra guard. See <c>ssh/</c> for the sshd side.
/// </summary>
public static class InternalSshEndpoints
{
    public sealed record AuthorizeRequest(long KeyId, string Operation, string RepoPath);
    private sealed record KeyLine(long Id, string PublicKey);

    public static void MapInternalSsh(this IEndpointRouteBuilder app, string internalToken)
    {
        var g = app.MapGroup("/internal/ssh");

        g.AddEndpointFilter(async (ctx, next) =>
        {
            if (string.IsNullOrEmpty(internalToken)
                || ctx.HttpContext.Request.Headers["X-Internal-Token"] != internalToken)
                return Results.StatusCode(403);
            return await next(ctx);
        });

        // sshd's AuthorizedKeysCommand: given the offered key's fingerprint, emit matching
        // authorized_keys lines with a forced command carrying the key id.
        g.MapGet("/authorized-keys", async (string fingerprint, Db db, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<KeyLine>(
                "SELECT id, public_key AS PublicKey FROM ssh_keys WHERE fingerprint = @fingerprint", new { fingerprint });

            var shell = cfg["GitSrv:SshShellPath"] ?? "/usr/local/bin/gitsrv-shell";
            var lines = rows.Select(r =>
                $"command=\"{shell} --key-id={r.Id}\",no-port-forwarding,no-agent-forwarding,no-X11-forwarding,no-pty {r.PublicKey}");
            return Results.Text(string.Join('\n', lines) + "\n", "text/plain");
        });

        // The forced command's authorization check. Returns the absolute on-disk path to exec against.
        g.MapPost("/authorize", async (AuthorizeRequest req, Db db, GitAccessService access, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var userId = await conn.QuerySingleOrDefaultAsync<long?>(
                "SELECT user_id FROM ssh_keys WHERE id = @keyId", new { req.KeyId });
            if (userId is null)
                return Results.Json(new { allowed = false, reason = "Unknown key." }, statusCode: 403);

            var op = req.Operation == "git-receive-pack" ? GitOperation.Write : GitOperation.Read;

            GitTarget? target;
            try
            {
                target = await access.ResolveAsync(req.RepoPath, userId, op, ct);
            }
            catch (Auth.ForbiddenException ex)
            {
                return Results.Json(new { allowed = false, reason = ex.Message }, statusCode: 403);
            }
            if (target is null)
                return Results.Json(new { allowed = false, reason = "Repository not found." }, statusCode: 404);

            await conn.ExecuteAsync("UPDATE ssh_keys SET last_used_at = now() WHERE id = @keyId", new { req.KeyId });

            return Results.Json(new
            {
                allowed = true,
                absolutePath = target.AbsolutePath,
                repoId = target.RepoId,
                orgId = target.OrgId,
            });
        });

        // Called by the shim after a successful receive-pack to refresh size accounting + PR state.
        g.MapPost("/pushed/{repoId:long}/{orgId:long}", async (long repoId, long orgId, GitStorage storage, Db db,
            PullRequestService prs, CancellationToken ct) =>
        {
            var size = storage.MeasureSize(orgId, repoId);
            await using var conn = await db.OpenAsync(ct);
            await conn.ExecuteAsync("UPDATE repositories SET size_bytes = @size, pushed_at = now() WHERE id = @repoId",
                new { size, repoId });
            await prs.SyncAfterPushAsync(repoId, storage.RepoPath(orgId, repoId), ct);
            return Results.NoContent();
        });
    }
}
