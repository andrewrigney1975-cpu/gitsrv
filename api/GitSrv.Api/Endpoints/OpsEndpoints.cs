using System.Text;
using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Data;
using GitSrv.Api.Domain;
using GitSrv.Api.Identity;
using GitSrv.Api.Ops;

namespace GitSrv.Api.Endpoints;

public static class OpsEndpoints
{
    public sealed record SettingRequest(string Value);

    public static void MapOps(this IEndpointRouteBuilder app)
    {
        // ---- Prometheus-style metrics (open; put it behind the proxy in production) ----
        app.MapGet("/metrics", async (Db db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var m = await conn.QuerySingleAsync<MetricRow>("""
                SELECT
                  (SELECT count(*) FROM users)::bigint          AS Users,
                  (SELECT count(*) FROM organisations)::bigint  AS Orgs,
                  (SELECT count(*) FROM repositories)::bigint   AS Repos,
                  (SELECT count(*) FROM pull_requests WHERE state = 'open')::bigint AS OpenPrs,
                  (SELECT count(*) FROM issues WHERE state = 'open')::bigint        AS OpenIssues,
                  (SELECT count(*) FROM workflow_runs)::bigint  AS WorkflowRuns,
                  (SELECT count(*) FROM workflow_jobs WHERE status = 'queued')::bigint AS QueuedJobs,
                  (SELECT COALESCE(sum(size_bytes), 0)::bigint FROM repositories)   AS RepoBytes
                """);
            var sb = new StringBuilder();
            void G(string name, string help, long v)
            {
                sb.Append("# HELP gitsrv_").Append(name).Append(' ').Append(help).Append('\n');
                sb.Append("# TYPE gitsrv_").Append(name).Append(" gauge\n");
                sb.Append("gitsrv_").Append(name).Append(' ').Append(v).Append('\n');
            }
            G("users_total", "Registered users.", m.Users);
            G("organisations_total", "Organisations.", m.Orgs);
            G("repositories_total", "Repositories.", m.Repos);
            G("pull_requests_open", "Open pull requests.", m.OpenPrs);
            G("issues_open", "Open issues.", m.OpenIssues);
            G("workflow_runs_total", "Workflow runs ever.", m.WorkflowRuns);
            G("workflow_jobs_queued", "Queued CI jobs.", m.QueuedJobs);
            G("repository_bytes", "On-disk size of all repositories.", m.RepoBytes);
            G("process_working_set_bytes", "Process working set.", Environment.WorkingSet);
            return Results.Text(sb.ToString(), "text/plain; version=0.0.4");
        });

        // ---- org audit log ----
        var org = app.MapGroup("/api/orgs/{slug}");
        org.MapGet("/audit", async (string slug, string? format, CurrentUser cu, OrgService orgs, Authorizer authz, AuditService audit, CancellationToken ct) =>
        {
            var o = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Organisation not found.");
            await authz.RequireOrgRoleAsync(cu.RequireId(), o.Id, OrgRole.Admin, ct);
            var entries = await audit.ForOrgAsync(o.Id, 1000, ct);
            if (format == "csv")
            {
                var sb = new StringBuilder("time,actor,action,target,detail,ip\n");
                foreach (var e in entries)
                    sb.Append($"{e.CreatedAt:o},{Csv(e.ActorName)},{Csv(e.Action)},{Csv(e.Target)},{Csv(e.Detail)},{Csv(e.Ip)}\n");
                return Results.Text(sb.ToString(), "text/csv");
            }
            return Results.Json(entries);
        });

        // ---- admin console (site admins) ----
        var admin = app.MapGroup("/api/admin").RequireAuth();
        admin.AddEndpointFilter(async (ctx, next) =>
        {
            var cu = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            if (!cu.IsSiteAdmin) throw new ForbiddenException("Site admin only.");
            return await next(ctx);
        });

        admin.MapGet("/overview", async (Db db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var counts = await conn.QuerySingleAsync<AdminOverview>("""
                SELECT (SELECT count(*) FROM users)::int AS Users, (SELECT count(*) FROM organisations)::int AS Orgs,
                       (SELECT count(*) FROM repositories)::int AS Repos, (SELECT count(*) FROM personal_access_tokens)::int AS Tokens,
                       (SELECT count(*) FROM ssh_keys)::int AS SshKeys
                """);
            var settings = (await conn.QueryAsync<Setting>("SELECT key, value FROM instance_settings")).ToDictionary(s => s.Key, s => s.Value);
            return Results.Json(new { counts, settings });
        });

        admin.MapGet("/users", async (string? q, Db db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Json(await conn.QueryAsync<AdminUser>("""
                SELECT id, username, email, display_name AS DisplayName, is_site_admin AS IsSiteAdmin, created_at AS CreatedAt
                FROM users WHERE (@q = '' OR username ILIKE '%' || @q || '%' OR email ILIKE '%' || @q || '%')
                ORDER BY created_at DESC LIMIT 100
                """, new { q = q ?? "" }));
        });

        admin.MapPost("/users/{id:long}/site-admin", async (long id, SettingRequest req, Db db, CurrentUser cu, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var makeAdmin = req.Value == "true";
            if (!makeAdmin && id == cu.RequireId()) throw new ValidationException("You cannot remove your own site-admin role.");
            await conn.ExecuteAsync("UPDATE users SET is_site_admin = @makeAdmin WHERE id = @id", new { id, makeAdmin });
            return Results.NoContent();
        });

        admin.MapGet("/orgs", async (Db db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Json(await conn.QueryAsync<AdminOrg>("""
                SELECT o.id, o.slug, o.name, o.created_at AS CreatedAt,
                       (SELECT count(*) FROM org_members m WHERE m.org_id = o.id)::int AS Members,
                       (SELECT count(*) FROM repositories r WHERE r.org_id = o.id)::int AS Repos
                FROM organisations o ORDER BY o.created_at DESC LIMIT 200
                """));
        });

        admin.MapPut("/settings/{key}", async (string key, SettingRequest req, Db db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var n = await conn.ExecuteAsync("UPDATE instance_settings SET value = @value WHERE key = @key", new { key, value = req.Value ?? "" });
            if (n == 0) throw new NotFoundException("Unknown setting.");
            return Results.NoContent();
        });
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n')
        ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private sealed record MetricRow(long Users, long Orgs, long Repos, long OpenPrs, long OpenIssues, long WorkflowRuns, long QueuedJobs, long RepoBytes);
    private sealed record AdminOverview(int Users, int Orgs, int Repos, int Tokens, int SshKeys);
    private sealed record Setting(string Key, string Value);
    private sealed record AdminUser(long Id, string Username, string Email, string DisplayName, bool IsSiteAdmin, DateTime CreatedAt);
    private sealed record AdminOrg(long Id, string Slug, string Name, DateTime CreatedAt, int Members, int Repos);
}
