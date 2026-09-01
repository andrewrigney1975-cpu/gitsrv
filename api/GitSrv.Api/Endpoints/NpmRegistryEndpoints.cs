using System.Text.Json.Nodes;
using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Data;
using GitSrv.Api.Packages;

namespace GitSrv.Api.Endpoints;

/// <summary>
/// npm registry, org-scoped: point <c>.npmrc</c> at <c>{base}/npm/{org}/</c>. Publish is
/// <c>PUT /npm/{org}/{name}</c> with the standard packument+_attachments body; install pulls the
/// packument and the tarball from <c>.../-/{tarball}</c>.
/// </summary>
public static class NpmRegistryEndpoints
{
    public static void MapNpmRegistry(this IEndpointRouteBuilder app, string publicBaseUrl)
    {
        app.MapMethods("/npm/{orgSlug}/{**rest}", ["GET", "PUT"], async (string orgSlug, string rest,
            HttpContext ctx, PackageService pkgs, IArtifactStore store, Db db, CancellationToken ct) =>
        {
            var orgId = await pkgs.ResolveOrgAsync(orgSlug, ct);
            rest = Uri.UnescapeDataString(rest);

            // tarball download:  {name}/-/{tarball}
            var dashIdx = rest.IndexOf("/-/", StringComparison.Ordinal);
            if (ctx.Request.Method == "GET" && dashIdx >= 0)
            {
                var pkgName = rest[..dashIdx];
                var tarball = rest[(dashIdx + 3)..];
                var userId = await pkgs.AuthenticateAsync(ctx, ct);
                var pkg = await pkgs.ResolveForReadAsync(orgId, "npm", pkgName, userId, ct);
                var file = await pkgs.FindFileAsync(pkg.Id, tarball, ct) ?? throw new NotFoundException("Tarball not found.");
                var s = await store.OpenAsync(file.StorageKey) ?? throw new NotFoundException("Tarball missing.");
                return Results.Stream(s, "application/octet-stream", fileDownloadName: tarball);
            }

            var name = rest.TrimEnd('/');

            if (ctx.Request.Method == "GET")
            {
                var userId = await pkgs.AuthenticateAsync(ctx, ct);
                var pkg = await pkgs.ResolveForReadAsync(orgId, "npm", name, userId, ct);
                // Prefer a forwarded host (keeps the port); fall back to the configured public URL.
                var fwdHost = ctx.Request.Headers["X-Forwarded-Host"].ToString();
                var reqBase = !string.IsNullOrEmpty(fwdHost)
                    ? $"{(ctx.Request.Headers["X-Forwarded-Proto"].ToString() is { Length: > 0 } p ? p : "http")}://{fwdHost}"
                    : publicBaseUrl;
                return Results.Content(await BuildPackumentAsync(db, pkg.Id, name, orgSlug, reqBase, ct), "application/json");
            }

            // PUT = publish
            var uid = await pkgs.AuthenticateAsync(ctx, ct) ?? throw new UnauthorizedException();
            await pkgs.RequireOrgMemberAsync(uid, orgId, ct);

            using var bodyReader = new StreamReader(ctx.Request.Body);
            var doc = JsonNode.Parse(await bodyReader.ReadToEndAsync(ct)) as JsonObject
                ?? throw new ValidationException("Malformed publish payload.");
            var pkgId = await pkgs.EnsurePackageAsync(orgId, "npm", name, uid, ct);

            var versions = doc["versions"]?.AsObject() ?? throw new ValidationException("No versions in payload.");
            foreach (var (ver, meta) in versions)
            {
                await pkgs.AddVersionAsync(pkgId, ver, meta!.ToJsonString(), uid, ct);
            }
            var attachments = doc["_attachments"]?.AsObject();
            if (attachments is not null)
            {
                foreach (var (fileName, att) in attachments)
                {
                    var data = att!["data"]?.GetValue<string>();
                    if (data is null) continue;
                    var bytes = Convert.FromBase64String(data);
                    var digest = "sha256:" + PackageService.Sha256Hex(bytes);
                    var key = await store.PutAsync(new MemoryStream(bytes), ct);
                    var verId = await ResolveVersionIdAsync(db, pkgId, fileName, ct);
                    await pkgs.AddFileAsync(pkgId, verId, fileName, digest, bytes.Length, "application/octet-stream", key, ct);
                }
            }
            return Results.Json(new { success = true }, statusCode: 201);
        });
    }

    private static async Task<long?> ResolveVersionIdAsync(Db db, long pkgId, string tarballName, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        // tarball names look like  name-1.2.3.tgz  or  @scope/name  ->  scope-name-1.2.3.tgz
        var m = System.Text.RegularExpressions.Regex.Match(tarballName, @"-(\d+\.\d+\.\d+[^/]*)\.tgz$");
        if (!m.Success) return null;
        return await conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT id FROM package_versions WHERE package_id = @pkgId AND version = @v", new { pkgId, v = m.Groups[1].Value });
    }

    private static async Task<string> BuildPackumentAsync(Db db, long pkgId, string name, string orgSlug, string baseUrl, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<VerRow>(
            "SELECT id, version, metadata FROM package_versions WHERE package_id = @pkgId AND NOT yanked ORDER BY created_at", new { pkgId }))
            .ToList();
        if (rows.Count == 0) throw new NotFoundException("No published versions.");

        var files = (await conn.QueryAsync<FileRow>(
            "SELECT name, digest, version_id AS VersionId FROM package_files WHERE package_id = @pkgId", new { pkgId })).ToList();

        var versions = new JsonObject();
        string latest = rows[^1].Version;
        foreach (var r in rows)
        {
            var meta = JsonNode.Parse(r.Metadata)!.AsObject();
            var file = files.FirstOrDefault(f => f.VersionId == r.Id);
            var tarballName = file?.Name ?? $"{LastSegment(name)}-{r.Version}.tgz";
            meta["dist"] = new JsonObject
            {
                ["tarball"] = $"{baseUrl}/npm/{orgSlug}/{Uri.EscapeDataString(name)}/-/{tarballName}",
                ["shasum"] = file?.Digest?.Replace("sha256:", "") ?? "",
                ["integrity"] = file?.Digest is null ? null : "sha256-" + Convert.ToBase64String(Convert.FromHexString(file.Digest["sha256:".Length..])),
            };
            versions[r.Version] = meta;
        }

        var packument = new JsonObject
        {
            ["name"] = name,
            ["dist-tags"] = new JsonObject { ["latest"] = latest },
            ["versions"] = versions,
        };
        return packument.ToJsonString();
    }

    private static string LastSegment(string s) => s.Contains('/') ? s[(s.LastIndexOf('/') + 1)..] : s;

    private sealed record VerRow(long Id, string Version, string Metadata);
    private sealed record FileRow(string Name, string Digest, long? VersionId);
}
