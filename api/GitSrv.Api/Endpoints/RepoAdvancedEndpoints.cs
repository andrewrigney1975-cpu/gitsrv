using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Data;
using GitSrv.Api.Git;
using LibGit2Sharp;

namespace GitSrv.Api.Endpoints;

public static class RepoAdvancedEndpoints
{
    public sealed record CreateBranchRequest(string Name, string From);
    public sealed record RenameBranchRequest(string To);
    public sealed record CommitOntoRequest(string Sha, string Branch);
    public sealed record EditFileRequest(string Branch, string Path, string Content, string Message, string? ExpectedBlobSha);
    public sealed record DeleteFileRequest(string Branch, string Path, string Message);
    public sealed record ProtectionRequest(string Pattern, bool RequirePullRequest, int RequiredApprovals,
        bool RequireStatusChecks, bool BlockForcePush, bool BlockDeletion, bool RequireLinearHistory, bool RestrictPush);
    public sealed record ReleaseRequest(string TagName, string Target, string Name, string Body, bool IsPrerelease, bool IsDraft);
    public sealed record WebhookRequest(string Url, string Secret, string Events, bool IsActive);
    public sealed record RepoConfigRequest(string? DefaultBranch, bool? AllowMergeCommit, bool? AllowSquash, bool? AllowRebase, bool? DeleteBranchOnMerge);

    public static void MapRepoAdvanced(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/orgs/{slug}/repos/{repoSlug}");

        // ---- branches ----
        g.MapPost("/branches", async (string slug, string repoSlug, CreateBranchRequest req, CurrentUser cu,
            RepoBrowseService browse, BranchOpsService ops, CancellationToken ct) =>
        {
            var b = await W(browse, slug, repoSlug, cu, ct);
            ops.CreateBranch(b.OrgId, b.RepoId, (req.Name ?? "").Trim(), (req.From ?? b.DefaultBranch).Trim());
            return Results.Json(new { name = req.Name }, statusCode: 201);
        });

        g.MapPost("/branches/{name}/rename", async (string slug, string repoSlug, string name, RenameBranchRequest req, CurrentUser cu,
            RepoBrowseService browse, BranchOpsService ops, CancellationToken ct) =>
        {
            var b = await W(browse, slug, repoSlug, cu, ct);
            await ops.RenameBranchAsync(b.OrgId, b.RepoId, name, (req.To ?? "").Trim(), ct);
            return Results.NoContent();
        });

        g.MapDelete("/branches/{name}", async (string slug, string repoSlug, string name, CurrentUser cu,
            RepoBrowseService browse, BranchOpsService ops, CancellationToken ct) =>
        {
            var b = await W(browse, slug, repoSlug, cu, ct);
            await ops.DeleteBranchAsync(b.OrgId, b.RepoId, name, b.DefaultBranch, ct);
            return Results.NoContent();
        });

        // ---- file & commit ops ----
        g.MapPost("/edit", async (string slug, string repoSlug, EditFileRequest req, CurrentUser cu,
            RepoBrowseService browse, BranchOpsService ops, AccountService accounts, CancellationToken ct) =>
        {
            var b = await W(browse, slug, repoSlug, cu, ct);
            var sig = await SigAsync(accounts, cu, ct);
            var r = await ops.EditFileAsync(b.OrgId, b.RepoId, req.Branch, req.Path, req.Content ?? "",
                req.Message ?? "", sig, req.ExpectedBlobSha, ct);
            return Results.Json(r);
        });

        g.MapPost("/delete-file", async (string slug, string repoSlug, DeleteFileRequest req, CurrentUser cu,
            RepoBrowseService browse, BranchOpsService ops, AccountService accounts, CancellationToken ct) =>
        {
            var b = await W(browse, slug, repoSlug, cu, ct);
            var r = await ops.DeleteFileAsync(b.OrgId, b.RepoId, req.Branch, req.Path, req.Message ?? "", await SigAsync(accounts, cu, ct), ct);
            return Results.Json(r);
        });

        g.MapPost("/cherry-pick", async (string slug, string repoSlug, CommitOntoRequest req, CurrentUser cu,
            RepoBrowseService browse, BranchOpsService ops, AccountService accounts, CancellationToken ct) =>
        {
            var b = await W(browse, slug, repoSlug, cu, ct);
            var r = await ops.CherryPickAsync(b.OrgId, b.RepoId, req.Sha, req.Branch, await SigAsync(accounts, cu, ct), ct);
            return Results.Json(r);
        });

        g.MapPost("/revert", async (string slug, string repoSlug, CommitOntoRequest req, CurrentUser cu,
            RepoBrowseService browse, BranchOpsService ops, AccountService accounts, CancellationToken ct) =>
        {
            var b = await W(browse, slug, repoSlug, cu, ct);
            var r = await ops.RevertAsync(b.OrgId, b.RepoId, req.Sha, req.Branch, await SigAsync(accounts, cu, ct), ct);
            return Results.Json(r);
        });

        // ---- branch protection (admin) ----
        g.MapGet("/protections", async (string slug, string repoSlug, CurrentUser cu, RepoBrowseService browse, BranchProtectionService bp, CancellationToken ct) =>
        {
            var b = await A(browse, slug, repoSlug, cu, ct);
            return Results.Json(await bp.ListAsync(b.RepoId, ct));
        });
        g.MapPost("/protections", async (string slug, string repoSlug, ProtectionRequest req, CurrentUser cu, RepoBrowseService browse, BranchProtectionService bp, CancellationToken ct) =>
        {
            var b = await A(browse, slug, repoSlug, cu, ct);
            var id = await bp.UpsertAsync(b.RepoId, null, ToBp(0, req), ct);
            return Results.Json(new { id }, statusCode: 201);
        });
        g.MapPut("/protections/{id:long}", async (string slug, string repoSlug, long id, ProtectionRequest req, CurrentUser cu, RepoBrowseService browse, BranchProtectionService bp, CancellationToken ct) =>
        {
            var b = await A(browse, slug, repoSlug, cu, ct);
            await bp.UpsertAsync(b.RepoId, id, ToBp(id, req), ct);
            return Results.NoContent();
        });
        g.MapDelete("/protections/{id:long}", async (string slug, string repoSlug, long id, CurrentUser cu, RepoBrowseService browse, BranchProtectionService bp, CancellationToken ct) =>
        {
            var b = await A(browse, slug, repoSlug, cu, ct);
            await bp.DeleteAsync(b.RepoId, id, ct);
            return Results.NoContent();
        });

        // ---- repo config (admin) ----
        g.MapPatch("/config", async (string slug, string repoSlug, RepoConfigRequest req, CurrentUser cu,
            RepoBrowseService browse, GitStorage storage, Db db, CancellationToken ct) =>
        {
            var b = await A(browse, slug, repoSlug, cu, ct);
            await using var conn = await db.OpenAsync(ct);
            if (!string.IsNullOrWhiteSpace(req.DefaultBranch))
            {
                using var repo = new LibGit2Sharp.Repository(storage.RepoPath(b.OrgId, b.RepoId));
                if (repo.Branches[req.DefaultBranch] is null) throw new ValidationException($"No branch '{req.DefaultBranch}'.");
                repo.Refs.UpdateTarget(repo.Refs.Head, $"refs/heads/{req.DefaultBranch}");
                await conn.ExecuteAsync("UPDATE repositories SET default_branch = @d WHERE id = @id", new { d = req.DefaultBranch, id = b.RepoId });
            }
            await conn.ExecuteAsync("""
                UPDATE repositories SET
                    allow_merge_commit = COALESCE(@AllowMergeCommit, allow_merge_commit),
                    allow_squash = COALESCE(@AllowSquash, allow_squash),
                    allow_rebase = COALESCE(@AllowRebase, allow_rebase),
                    delete_branch_on_merge = COALESCE(@DeleteBranchOnMerge, delete_branch_on_merge)
                WHERE id = @id
                """, new { req.AllowMergeCommit, req.AllowSquash, req.AllowRebase, req.DeleteBranchOnMerge, id = b.RepoId });
            return Results.NoContent();
        });

        // ---- releases ----
        g.MapGet("/releases", async (string slug, string repoSlug, CurrentUser cu, RepoBrowseService browse, ReleaseService rel, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(await rel.ListAsync(b.RepoId, b.Permission >= RepoPermission.Write, ct));
        });
        g.MapGet("/releases/{tag}", async (string slug, string repoSlug, string tag, CurrentUser cu, RepoBrowseService browse, ReleaseService rel, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(await rel.GetAsync(b.RepoId, tag, ct));
        });
        g.MapPost("/releases", async (string slug, string repoSlug, ReleaseRequest req, CurrentUser cu,
            RepoBrowseService browse, ReleaseService rel, AccountService accounts, CancellationToken ct) =>
        {
            var b = await W(browse, slug, repoSlug, cu, ct);
            var u = (await accounts.GetAsync(cu.RequireId(), ct))!;
            var id = await rel.CreateAsync(b.OrgId, b.RepoId, u.Id, (req.TagName ?? "").Trim(), (req.Target ?? b.DefaultBranch).Trim(),
                req.Name ?? "", req.Body ?? "", req.IsPrerelease, req.IsDraft, u.Username, u.Email, ct);
            return Results.Json(new { id }, statusCode: 201);
        });
        g.MapDelete("/releases/{tag}", async (string slug, string repoSlug, string tag, bool? deleteTag, CurrentUser cu,
            RepoBrowseService browse, ReleaseService rel, CancellationToken ct) =>
        {
            var b = await W(browse, slug, repoSlug, cu, ct);
            await rel.DeleteAsync(b.OrgId, b.RepoId, tag, deleteTag ?? false, ct);
            return Results.NoContent();
        });
        g.MapPost("/releases/{tag}/assets", async (string slug, string repoSlug, string tag, HttpRequest request, CurrentUser cu,
            RepoBrowseService browse, ReleaseService rel, CancellationToken ct) =>
        {
            var b = await W(browse, slug, repoSlug, cu, ct);
            if (!request.HasFormContentType) throw new ValidationException("Upload the asset as multipart/form-data.");
            var file = request.Form.Files.FirstOrDefault() ?? throw new ValidationException("No file supplied.");
            await using var s = file.OpenReadStream();
            var asset = await rel.AddAssetAsync(b.OrgId, b.RepoId, tag, file.FileName, file.ContentType ?? "application/octet-stream",
                s, 200L * 1024 * 1024, ct);
            return Results.Json(asset, statusCode: 201);
        });
        g.MapGet("/releases/{tag}/assets/{assetId:long}", async (string slug, string repoSlug, string tag, long assetId, CurrentUser cu,
            RepoBrowseService browse, ReleaseService rel, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            var (stream, name, contentType) = await rel.OpenAssetAsync(b.RepoId, tag, assetId, ct);
            return Results.Stream(stream, contentType, fileDownloadName: name);
        });

        // ---- webhooks (admin) ----
        g.MapGet("/hooks", async (string slug, string repoSlug, CurrentUser cu, RepoBrowseService browse, WebhookService wh, CancellationToken ct) =>
        {
            var b = await A(browse, slug, repoSlug, cu, ct);
            return Results.Json(await wh.ListAsync(b.RepoId, ct));
        });
        g.MapPost("/hooks", async (string slug, string repoSlug, WebhookRequest req, CurrentUser cu, RepoBrowseService browse, WebhookService wh, CancellationToken ct) =>
        {
            var b = await A(browse, slug, repoSlug, cu, ct);
            var id = await wh.CreateAsync(b.RepoId, req.Url ?? "", req.Secret ?? "", req.Events ?? "push", req.IsActive, ct);
            return Results.Json(new { id }, statusCode: 201);
        });
        g.MapDelete("/hooks/{id:long}", async (string slug, string repoSlug, long id, CurrentUser cu, RepoBrowseService browse, WebhookService wh, CancellationToken ct) =>
        {
            var b = await A(browse, slug, repoSlug, cu, ct);
            await wh.DeleteAsync(b.RepoId, id, ct);
            return Results.NoContent();
        });
        g.MapGet("/hooks/{id:long}/deliveries", async (string slug, string repoSlug, long id, CurrentUser cu, RepoBrowseService browse, WebhookService wh, CancellationToken ct) =>
        {
            var b = await A(browse, slug, repoSlug, cu, ct);
            return Results.Json(await wh.DeliveriesAsync(b.RepoId, id, ct));
        });
    }

    private static BranchProtection ToBp(long id, ProtectionRequest r) => new(id, (r.Pattern ?? "").Trim(),
        r.RequirePullRequest, Math.Clamp(r.RequiredApprovals, 0, 10), r.RequireStatusChecks, r.BlockForcePush,
        r.BlockDeletion, r.RequireLinearHistory, r.RestrictPush);

    private static async Task<LibGit2Sharp.Signature> SigAsync(AccountService accounts, CurrentUser cu, CancellationToken ct)
    {
        var u = (await accounts.GetAsync(cu.RequireId(), ct))!;
        return new LibGit2Sharp.Signature(u.Username, string.IsNullOrWhiteSpace(u.Email) ? $"{u.Username}@users.noreply.gitsrv" : u.Email, DateTimeOffset.Now);
    }

    private static Task<BrowseContext> W(RepoBrowseService b, string s, string r, CurrentUser cu, CancellationToken ct) => Require(b, s, r, cu, RepoPermission.Write, ct);
    private static Task<BrowseContext> A(RepoBrowseService b, string s, string r, CurrentUser cu, CancellationToken ct) => Require(b, s, r, cu, RepoPermission.Admin, ct);

    private static async Task<BrowseContext> Require(RepoBrowseService browse, string slug, string repoSlug, CurrentUser cu, RepoPermission min, CancellationToken ct)
    {
        if (!cu.IsAuthenticated) throw new UnauthorizedException();
        var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
        if (b.Permission < min) throw new ForbiddenException();
        return b;
    }
}
