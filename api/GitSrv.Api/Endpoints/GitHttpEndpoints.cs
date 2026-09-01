using System.Net.Http.Headers;
using System.Text;
using GitSrv.Api.Auth;
using GitSrv.Api.Git;

namespace GitSrv.Api.Endpoints;

/// <summary>
/// Git Smart-HTTP transport, served at the repo's clone URL root (<c>/{org}/{repo}.git/…</c>), not
/// under <c>/api</c>. Auth is HTTP Basic: password is a personal access token (preferred) or the
/// account password. Anonymous is allowed for fetch/clone of public repos only.
/// </summary>
public static class GitHttpEndpoints
{
    internal sealed record Principal(long? UserId, bool CanRead, bool CanWrite);

    public static void MapGitHttp(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{org}/{repo}/info/refs", async (string org, string repo, string? service,
            HttpContext ctx, GitAuthResolver auth, GitAccessService access, GitBackend backend, CancellationToken ct) =>
        {
            if (!GitBackend.IsValidService(service))
                return Results.Text("Dumb HTTP transport is not supported. Use a current git client.", statusCode: 400);

            var op = service == "git-receive-pack" ? GitOperation.Write : GitOperation.Read;
            var principal = await auth.ResolveAsync(ctx, ct);
            if (!Allowed(principal, op))
                return principal.UserId is null ? Challenge(ctx)
                    : Results.Text("Token scope does not permit this operation.", statusCode: 403);

            var target = await access.ResolveAsync($"{org}/{repo}", principal.UserId, op, ct);
            if (target is null)
                return principal.UserId is null ? Challenge(ctx) : Results.NotFound();

            await backend.AdvertiseAsync(ctx, service!, target.AbsolutePath, GitProtocol(ctx), ct);
            return Results.Empty;
        });

        app.MapPost("/{org}/{repo}/git-upload-pack", (string org, string repo, HttpContext ctx,
            GitAuthResolver auth, GitAccessService access, GitBackend backend, CancellationToken ct)
            => Rpc(org, repo, "git-upload-pack", GitOperation.Read, ctx, auth, access, backend, null, null, null, ct));

        app.MapPost("/{org}/{repo}/git-receive-pack", (string org, string repo, HttpContext ctx,
            GitAuthResolver auth, GitAccessService access, GitBackend backend, GitStorage storage,
            PullRequestService prs, IConfiguration cfg, CancellationToken ct)
            => Rpc(org, repo, "git-receive-pack", GitOperation.Write, ctx, auth, access, backend, storage, prs, cfg, ct));
    }

    private static async Task<IResult> Rpc(string org, string repo, string service, GitOperation op,
        HttpContext ctx, GitAuthResolver auth, GitAccessService access, GitBackend backend, GitStorage? storage,
        PullRequestService? prs, IConfiguration? cfg, CancellationToken ct)
    {
        if (op == GitOperation.Write && storage is not null && storage.MaxPushBytes > 0
            && ctx.Request.ContentLength is { } len && len > storage.MaxPushBytes)
        {
            return Results.Text($"Push rejected: exceeds the {storage.MaxPushBytes / (1024 * 1024)} MiB per-push limit.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var principal = await auth.ResolveAsync(ctx, ct);
        if (!Allowed(principal, op))
            return principal.UserId is null ? Challenge(ctx)
                : Results.Text("Token scope does not permit this operation.", statusCode: 403);

        var target = await access.ResolveAsync($"{org}/{repo}", principal.UserId, op, ct);
        if (target is null)
            return principal.UserId is null ? Challenge(ctx) : Results.NotFound();

        IReadOnlyDictionary<string, string>? hookEnv = null;
        if (op == GitOperation.Write && cfg is not null)
            hookEnv = new Dictionary<string, string>
            {
                ["GITSRV_API_BASE"] = "http://localhost:8080",
                ["GITSRV_INTERNAL_TOKEN"] = cfg["GitSrv:InternalToken"] ?? "",
                ["GITSRV_REPO_ID"] = target.RepoId.ToString(),
                ["GITSRV_PUSHER_ID"] = principal.UserId?.ToString() ?? "",
            };

        await backend.RpcAsync(ctx, service, target.AbsolutePath, GitProtocol(ctx), ct, hookEnv);

        if (op == GitOperation.Write)
        {
            await access.RecordPushAsync(target, CancellationToken.None);
            if (prs is not null)
                await prs.SyncAfterPushAsync(target.RepoId, target.AbsolutePath, CancellationToken.None);
        }

        return Results.Empty;
    }

    private static bool Allowed(Principal p, GitOperation op) => op == GitOperation.Write ? p.CanWrite : p.CanRead;

    private static IResult Challenge(HttpContext ctx)
    {
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"GitSrv\", charset=\"UTF-8\"";
        return Results.Unauthorized();
    }

    private static string? GitProtocol(HttpContext ctx)
    {
        var v = ctx.Request.Headers["Git-Protocol"].ToString();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    // ---- Basic-auth resolution (registered in DI) ----
    public sealed class GitAuthResolver(AccountService accounts, PatService pats)
    {
        internal async Task<Principal> ResolveAsync(HttpContext ctx, CancellationToken ct)
        {
            // No credentials at all: allowed to *attempt* a read (GitAccessService enforces
            // visibility and challenges if the repo isn't public). Writes still require auth.
            var anonymous = new Principal(null, CanRead: true, CanWrite: false);
            // Credentials supplied but wrong: force a fresh challenge, don't fall back to anon.
            var rejected = new Principal(null, CanRead: false, CanWrite: false);

            var header = ctx.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                return anonymous;

            string user, secret;
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var i = decoded.IndexOf(':');
                if (i < 0) return rejected;
                user = decoded[..i];
                secret = decoded[(i + 1)..];
            }
            catch (FormatException)
            {
                return rejected;
            }

            if (secret.StartsWith(PatService.Prefix, StringComparison.Ordinal))
            {
                var v = await pats.VerifyAsync(secret, ct);
                return v is null ? rejected : new Principal(v.UserId, v.ScopeRead, v.ScopeWrite);
            }

            var u = await accounts.VerifyCredentialsAsync(user, secret, ct);
            return u is null ? rejected : new Principal(u.Id, true, true);
        }
    }
}
