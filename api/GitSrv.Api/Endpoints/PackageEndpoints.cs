using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Data;
using GitSrv.Api.Packages;
using GitSrv.Api.Identity;

namespace GitSrv.Api.Endpoints;

public static class PackageEndpoints
{
    public sealed record VisibilityRequest(string Visibility);

    public static void MapPackages(this IEndpointRouteBuilder app, string publicBaseUrl)
    {
        // ---- generic registry ----
        app.MapMethods("/generic/{orgSlug}/{name}/{version}/{file}", ["GET", "PUT"], async (
            string orgSlug, string name, string version, string file, HttpContext ctx,
            PackageService pkgs, IArtifactStore store, CancellationToken ct) =>
        {
            var orgId = await pkgs.ResolveOrgAsync(orgSlug, ct);
            if (ctx.Request.Method == "PUT")
            {
                var uid = await pkgs.AuthenticateAsync(ctx, ct) ?? throw new UnauthorizedException();
                await pkgs.RequireOrgMemberAsync(uid, orgId, ct);
                var pkgId = await pkgs.EnsurePackageAsync(orgId, "generic", name, uid, ct);
                var verId = await pkgs.AddVersionAsync(pkgId, version, "{}", uid, ct);
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();
                var key = await store.PutAsync(new MemoryStream(bytes), ct);
                await pkgs.AddFileAsync(pkgId, verId, file, "sha256:" + PackageService.Sha256Hex(bytes), bytes.Length,
                    ctx.Request.ContentType ?? "application/octet-stream", key, ct);
                return Results.StatusCode(201);
            }

            var userId = await pkgs.AuthenticateAsync(ctx, ct);
            var pkg = await pkgs.ResolveForReadAsync(orgId, "generic", name, userId, ct);
            var f = await pkgs.FindFileAsync(pkg.Id, file, ct) ?? throw new NotFoundException("File not found.");
            var s = await store.OpenAsync(f.StorageKey) ?? throw new NotFoundException("File missing.");
            return Results.Stream(s, f.ContentType, fileDownloadName: file);
        });

        // ---- browse (UI / API) ----
        var g = app.MapGroup("/api/orgs/{slug}");

        g.MapGet("/packages", async (string slug, CurrentUser cu, OrgService orgs, PackageService pkgs, CancellationToken ct) =>
        {
            var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Organisation not found.");
            var list = await pkgs.ListForOrgAsync(org.Id, cu.UserId, ct);
            var storage = await pkgs.OrgStorageBytesAsync(org.Id, ct);
            return Results.Json(new { packages = list, storageBytes = storage });
        });

        g.MapGet("/packages/{kind}/{name}", async (string slug, string kind, string name, CurrentUser cu,
            OrgService orgs, PackageService pkgs, Db db, CancellationToken ct) =>
        {
            var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Organisation not found.");
            var pkg = await pkgs.ResolveForReadAsync(org.Id, kind, Uri.UnescapeDataString(name), cu.UserId, ct);
            var versions = await pkgs.ListVersionsAsync(pkg.Id, ct);
            await using var conn = await db.OpenAsync(ct);
            var files = (await conn.QueryAsync<PackageFileRow>("""
                SELECT id, name, digest, size_bytes AS SizeBytes, content_type AS ContentType, storage_key AS StorageKey
                FROM package_files WHERE package_id = @id ORDER BY created_at DESC
                """, new { id = pkg.Id })).ToList();
            return Results.Json(new
            {
                pkg.Kind, pkg.Name, pkg.Visibility, versions,
                files = files.Select(f => new { f.Name, f.Digest, f.SizeBytes, f.ContentType }),
                install = InstallHint(pkg.Kind, slug, pkg.Name, versions.FirstOrDefault()?.Version, publicBaseUrl),
            });
        });

        g.MapPatch("/packages/{kind}/{name}", async (string slug, string kind, string name, VisibilityRequest req,
            CurrentUser cu, OrgService orgs, Authorizer authz, PackageService pkgs, CancellationToken ct) =>
        {
            var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Organisation not found.");
            await authz.RequireOrgRoleAsync(cu.RequireId(), org.Id, Domain.OrgRole.Admin, ct);
            await pkgs.SetVisibilityAsync(org.Id, kind, Uri.UnescapeDataString(name), req.Visibility ?? "private", ct);
            return Results.NoContent();
        });

        g.MapDelete("/packages/{kind}/{name}", async (string slug, string kind, string name, CurrentUser cu,
            OrgService orgs, Authorizer authz, PackageService pkgs, IArtifactStore store, CancellationToken ct) =>
        {
            var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Organisation not found.");
            await authz.RequireOrgRoleAsync(cu.RequireId(), org.Id, Domain.OrgRole.Admin, ct);
            var pkg = await pkgs.GetAsync(org.Id, kind, Uri.UnescapeDataString(name), ct) ?? throw new NotFoundException("Package not found.");
            await pkgs.DeleteAsync(pkg.Id, store, ct);
            return Results.NoContent();
        });
    }

    private static string InstallHint(string kind, string org, string name, string? version, string baseUrl) => kind switch
    {
        "npm" => $"npm install {name} --registry {baseUrl}/npm/{org}/",
        "generic" => $"curl -O {baseUrl}/generic/{org}/{name}/{version}/<file>",
        "oci" => $"docker pull {new Uri(baseUrl).Authority}/{org}/{name}:{version ?? "latest"}",
        _ => $"{baseUrl}/{kind}/{org}/{name}",
    };
}
