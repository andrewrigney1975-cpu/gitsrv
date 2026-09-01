using GitSrv.Api.Actions;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Git;
using GitSrv.Api.Identity;

namespace GitSrv.Api.Endpoints;

public static class ActionsEndpoints
{
    public sealed record SecretRequest(string Name, string Value);
    public sealed record StatusRequest(string Context, string State, string Description, string TargetUrl);

    public static void MapActions(this IEndpointRouteBuilder app, string publicBaseUrl)
    {
        var g = app.MapGroup("/api/orgs/{slug}/repos/{repoSlug}");

        g.MapGet("/actions", async (string slug, string repoSlug, CurrentUser cu, RepoBrowseService browse, ActionsService actions, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(await actions.ListRunsAsync(b.RepoId, ct));
        });

        g.MapGet("/actions/{number:int}", async (string slug, string repoSlug, int number, CurrentUser cu, RepoBrowseService browse, ActionsService actions, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(await actions.GetRunAsync(b.RepoId, number, ct));
        });

        g.MapGet("/actions/{number:int}/jobs/{jobId:long}/logs", async (string slug, string repoSlug, int number, long jobId, long? after,
            CurrentUser cu, RepoBrowseService browse, ActionsService actions, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(await actions.JobLogsAsync(b.RepoId, jobId, after ?? 0, ct));
        });

        g.MapPost("/actions/{number:int}/rerun", async (string slug, string repoSlug, int number, CurrentUser cu,
            RepoBrowseService browse, ActionsService actions, CancellationToken ct) =>
        {
            var b = await RequireWrite(browse, slug, repoSlug, cu, ct);
            await actions.RerunAsync(b.OrgId, b.OrgSlug, b.RepoSlug, b.RepoId, publicBaseUrl, number, cu.RequireId(), ct);
            return Results.NoContent();
        });

        // ---- commit statuses / checks ----
        g.MapGet("/statuses/{sha}", async (string slug, string repoSlug, string sha, CurrentUser cu, RepoBrowseService browse, ChecksService checks, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(await checks.ForShaAsync(b.RepoId, sha, ct));
        });
        g.MapPost("/statuses/{sha}", async (string slug, string repoSlug, string sha, StatusRequest req, CurrentUser cu,
            RepoBrowseService browse, ChecksService checks, CancellationToken ct) =>
        {
            var b = await RequireWrite(browse, slug, repoSlug, cu, ct);
            if (req.State is not ("pending" or "success" or "failure" or "error")) throw new ValidationException("Invalid state.");
            await checks.SetAsync(b.RepoId, sha, req.Context ?? "default", req.State, req.Description ?? "", req.TargetUrl ?? "", ct);
            return Results.NoContent();
        });

        // ---- repo secrets (admin) ----
        g.MapGet("/secrets", async (string slug, string repoSlug, CurrentUser cu, RepoBrowseService browse, SecretsService secrets, CancellationToken ct) =>
        {
            var b = await RequireAdmin(browse, slug, repoSlug, cu, ct);
            return Results.Json(await secrets.ListAsync("repo", b.RepoId, ct));
        });
        g.MapPut("/secrets", async (string slug, string repoSlug, SecretRequest req, CurrentUser cu, RepoBrowseService browse, SecretsService secrets, CancellationToken ct) =>
        {
            var b = await RequireAdmin(browse, slug, repoSlug, cu, ct);
            await secrets.SetAsync("repo", b.RepoId, req.Name ?? "", req.Value ?? "", ct);
            return Results.NoContent();
        });
        g.MapDelete("/secrets/{name}", async (string slug, string repoSlug, string name, CurrentUser cu, RepoBrowseService browse, SecretsService secrets, CancellationToken ct) =>
        {
            var b = await RequireAdmin(browse, slug, repoSlug, cu, ct);
            await secrets.DeleteAsync("repo", b.RepoId, name, ct);
            return Results.NoContent();
        });

        // ---- org secrets (org admin) ----
        app.MapGet("/api/orgs/{slug}/secrets", async (string slug, CurrentUser cu, OrgService orgs, Authorizer authz, SecretsService secrets, CancellationToken ct) =>
        {
            var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Organisation not found.");
            await authz.RequireOrgRoleAsync(cu.RequireId(), org.Id, Domain.OrgRole.Admin, ct);
            return Results.Json(await secrets.ListAsync("org", org.Id, ct));
        });
        app.MapPut("/api/orgs/{slug}/secrets", async (string slug, SecretRequest req, CurrentUser cu, OrgService orgs, Authorizer authz, SecretsService secrets, CancellationToken ct) =>
        {
            var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Organisation not found.");
            await authz.RequireOrgRoleAsync(cu.RequireId(), org.Id, Domain.OrgRole.Admin, ct);
            await secrets.SetAsync("org", org.Id, req.Name ?? "", req.Value ?? "", ct);
            return Results.NoContent();
        });
        app.MapDelete("/api/orgs/{slug}/secrets/{name}", async (string slug, string name, CurrentUser cu, OrgService orgs, Authorizer authz, SecretsService secrets, CancellationToken ct) =>
        {
            var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Organisation not found.");
            await authz.RequireOrgRoleAsync(cu.RequireId(), org.Id, Domain.OrgRole.Admin, ct);
            await secrets.DeleteAsync("org", org.Id, name, ct);
            return Results.NoContent();
        });
    }

    private static async Task<BrowseContext> RequireWrite(RepoBrowseService browse, string slug, string repoSlug, CurrentUser cu, CancellationToken ct)
        => await Require(browse, slug, repoSlug, cu, RepoPermission.Write, ct);
    private static async Task<BrowseContext> RequireAdmin(RepoBrowseService browse, string slug, string repoSlug, CurrentUser cu, CancellationToken ct)
        => await Require(browse, slug, repoSlug, cu, RepoPermission.Admin, ct);

    private static async Task<BrowseContext> Require(RepoBrowseService browse, string slug, string repoSlug, CurrentUser cu, RepoPermission min, CancellationToken ct)
    {
        if (!cu.IsAuthenticated) throw new UnauthorizedException();
        var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
        if (b.Permission < min) throw new ForbiddenException();
        return b;
    }
}

public static class RunnerEndpoints
{
    public sealed record LogBatch(int? StepNumber, string[] Lines);
    public sealed record StepUpdate(string Status, string? Conclusion, int? ExitCode);
    public sealed record JobComplete(string Conclusion);

    public static void MapRunner(this IEndpointRouteBuilder app, string internalToken, string publicBaseUrl)
    {
        var g = app.MapGroup("/internal/runner");
        g.AddEndpointFilter(async (ctx, next) =>
            string.IsNullOrEmpty(internalToken) || ctx.HttpContext.Request.Headers["X-Internal-Token"] != internalToken
                ? Results.StatusCode(403) : await next(ctx));

        g.MapPost("/claim", async (string? runnerId, ActionsService actions, CancellationToken ct) =>
        {
            var job = await actions.ClaimAsync(runnerId ?? "runner", publicBaseUrl, ct);
            return job is null ? Results.NoContent() : Results.Json(job);
        });

        g.MapPost("/jobs/{jobId:long}/logs", async (long jobId, string token, LogBatch batch, ActionsService actions, CancellationToken ct) =>
        {
            await actions.ValidateJobTokenAsync(token, jobId, ct);
            await actions.AppendLogAsync(jobId, batch.StepNumber, batch.Lines ?? [], ct);
            return Results.NoContent();
        });

        g.MapPost("/jobs/{jobId:long}/steps/{number:int}", async (long jobId, int number, string token, StepUpdate upd, ActionsService actions, CancellationToken ct) =>
        {
            await actions.ValidateJobTokenAsync(token, jobId, ct);
            await actions.UpdateStepAsync(jobId, number, upd.Status, upd.Conclusion, upd.ExitCode, ct);
            return Results.NoContent();
        });

        g.MapPost("/jobs/{jobId:long}/complete", async (long jobId, string token, JobComplete done, ActionsService actions, CancellationToken ct) =>
        {
            await actions.ValidateJobTokenAsync(token, jobId, ct);
            await actions.CompleteJobAsync(jobId, done.Conclusion is "success" or "failure" or "cancelled" ? done.Conclusion : "failure", ct);
            return Results.NoContent();
        });
    }
}
