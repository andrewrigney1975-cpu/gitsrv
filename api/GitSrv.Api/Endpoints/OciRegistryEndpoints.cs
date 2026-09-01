using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Data;
using GitSrv.Api.Packages;

namespace GitSrv.Api.Endpoints;

/// <summary>
/// OCI / Docker registry v2, org-scoped: image names are <c>{org}/{name}</c>. Supports the blob
/// upload flow (monolithic + chunked), manifest push/pull by tag or digest, and tags list.
/// Auth is HTTP Basic with a personal access token or account password.
/// </summary>
public static class OciRegistryEndpoints
{
    private static IResult Unauth(HttpContext ctx)
    {
        ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"GitSrv\"";
        ctx.Response.Headers["Docker-Distribution-Api-Version"] = "registry/2.0";
        return Results.Json(new { errors = new[] { new { code = "UNAUTHORIZED", message = "authentication required" } } }, statusCode: 401);
    }

    public static void MapOciRegistry(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/v2/", ["GET"], async (HttpContext ctx, PackageService pkgs, CancellationToken ct) =>
        {
            var uid = await pkgs.AuthenticateAsync(ctx, ct);
            if (uid is null) return Unauth(ctx);
            ctx.Response.Headers["Docker-Distribution-Api-Version"] = "registry/2.0";
            return Results.Json(new { });
        });

        app.MapMethods("/v2/{**path}", ["GET", "HEAD", "POST", "PATCH", "PUT", "DELETE"], async (
            string path, HttpContext ctx, PackageService pkgs, IArtifactStore store, Db db, CancellationToken ct) =>
        {
            var uid = await pkgs.AuthenticateAsync(ctx, ct);
            var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            int verbIdx = Array.FindLastIndex(segs, s => s is "blobs" or "manifests" or "tags");
            if (verbIdx < 1) return Results.NotFound();
            var nameParts = segs[..verbIdx];
            var orgSlug = nameParts[0];
            var imageName = string.Join('/', nameParts[1..]);
            if (imageName.Length == 0) return Results.NotFound();
            var verb = segs[verbIdx];
            var tail = segs[(verbIdx + 1)..];

            long orgId;
            try { orgId = await pkgs.ResolveOrgAsync(orgSlug, ct); }
            catch (NotFoundException) { return Results.NotFound(); }

            var isWrite = ctx.Request.Method is "POST" or "PATCH" or "PUT" or "DELETE";
            if (isWrite)
            {
                if (uid is null) return Unauth(ctx);
                await pkgs.RequireOrgMemberAsync(uid.Value, orgId, ct);
            }

            var pkgId = isWrite
                ? await pkgs.EnsurePackageAsync(orgId, "oci", imageName, uid!.Value, ct)
                : (await pkgs.GetAsync(orgId, "oci", imageName, ct))?.Id ?? -1;

            if (!isWrite)
            {
                if (pkgId < 0) return Results.NotFound();
                var pk = await pkgs.GetAsync(orgId, "oci", imageName, ct)!;
                if (pk!.Visibility != "public")
                {
                    if (uid is null) return Unauth(ctx);
                    await pkgs.RequireOrgMemberAsync(uid.Value, orgId, ct);
                }
            }

            await using var conn = await db.OpenAsync(ct);

            // ---- blobs ----
            if (verb == "blobs")
            {
                // /blobs/uploads/  or  /blobs/uploads/{uuid}
                if (tail.Length >= 1 && tail[0] == "uploads")
                {
                    if (ctx.Request.Method == "POST")
                    {
                        var digestParam = ctx.Request.Query["digest"].ToString();
                        if (!string.IsNullOrEmpty(digestParam))
                        {
                            // monolithic upload
                            using var ms = new MemoryStream();
                            await ctx.Request.Body.CopyToAsync(ms, ct);
                            var bytes = ms.ToArray();
                            if ("sha256:" + PackageService.Sha256Hex(bytes) != digestParam)
                                return DockerError(400, "DIGEST_INVALID", "digest mismatch");
                            var key = await store.PutAsync(new MemoryStream(bytes), ct);
                            await pkgs.AddFileAsync(pkgId, null, digestParam, digestParam, bytes.Length, "application/octet-stream", key, ct);
                            ctx.Response.Headers["Location"] = $"/v2/{orgSlug}/{imageName}/blobs/{digestParam}";
                            ctx.Response.Headers["Docker-Content-Digest"] = digestParam;
                            return Results.StatusCode(201);
                        }
                        var uuid = Guid.NewGuid().ToString("N");
                        var tempKey = await store.PutTempAsync(Stream.Null, ct);
                        await conn.ExecuteAsync("INSERT INTO oci_uploads (uuid, package_id, storage_key) VALUES (@uuid, @pkgId, @tempKey)", new { uuid, pkgId, tempKey });
                        ctx.Response.Headers["Location"] = $"/v2/{orgSlug}/{imageName}/blobs/uploads/{uuid}";
                        ctx.Response.Headers["Docker-Upload-UUID"] = uuid;
                        ctx.Response.Headers["Range"] = "0-0";
                        return Results.StatusCode(202);
                    }

                    var uid2 = tail.Length >= 2 ? tail[1] : "";
                    var up = await conn.QuerySingleOrDefaultAsync<UploadRow>(
                        "SELECT uuid, storage_key AS StorageKey FROM oci_uploads WHERE uuid = @uid2 AND package_id = @pkgId", new { uid2, pkgId });
                    if (up is null) return Results.NotFound();

                    if (ctx.Request.Method == "PATCH")
                    {
                        await store.AppendTempAsync(up.StorageKey, ctx.Request.Body, ct);
                        var size = await store.TempSizeAsync(up.StorageKey);
                        ctx.Response.Headers["Location"] = $"/v2/{orgSlug}/{imageName}/blobs/uploads/{uid2}";
                        ctx.Response.Headers["Docker-Upload-UUID"] = uid2;
                        ctx.Response.Headers["Range"] = $"0-{Math.Max(0, size - 1)}";
                        return Results.StatusCode(202);
                    }
                    if (ctx.Request.Method == "PUT")
                    {
                        if (ctx.Request.ContentLength is > 0)
                            await store.AppendTempAsync(up.StorageKey, ctx.Request.Body, ct);
                        var digest = ctx.Request.Query["digest"].ToString();
                        var size = await store.TempSizeAsync(up.StorageKey);
                        var permKey = await store.PromoteTempAsync(up.StorageKey, ct);
                        await conn.ExecuteAsync("DELETE FROM oci_uploads WHERE uuid = @uid2", new { uid2 });
                        await pkgs.AddFileAsync(pkgId, null, digest, digest, size, "application/octet-stream", permKey, ct);
                        ctx.Response.Headers["Docker-Content-Digest"] = digest;
                        ctx.Response.Headers["Location"] = $"/v2/{orgSlug}/{imageName}/blobs/{digest}";
                        return Results.StatusCode(201);
                    }
                    return Results.StatusCode(405);
                }

                // /blobs/{digest}
                var wantDigest = tail.Length >= 1 ? tail[0] : "";
                var file = await pkgs.FindFileAsync(pkgId, wantDigest, ct);
                if (file is null) return DockerError(404, "BLOB_UNKNOWN", "blob unknown");
                ctx.Response.Headers["Docker-Content-Digest"] = wantDigest;
                if (ctx.Request.Method == "HEAD")
                {
                    ctx.Response.Headers["Content-Length"] = file.SizeBytes.ToString();
                    return Results.StatusCode(200);
                }
                var s = await store.OpenAsync(file.StorageKey) ?? throw new NotFoundException("blob missing");
                return Results.Stream(s, file.ContentType);
            }

            // ---- manifests ----
            if (verb == "manifests")
            {
                var reference = tail.Length >= 1 ? tail[0] : "latest";

                if (ctx.Request.Method is "GET" or "HEAD")
                {
                    string? digest = reference.StartsWith("sha256:")
                        ? reference
                        : await conn.QuerySingleOrDefaultAsync<string?>(
                            "SELECT manifest_digest FROM oci_tags WHERE package_id = @pkgId AND tag = @reference", new { pkgId, reference });
                    if (digest is null) return DockerError(404, "MANIFEST_UNKNOWN", "manifest unknown");
                    var mf = await pkgs.FindFileAsync(pkgId, digest, ct);
                    if (mf is null) return DockerError(404, "MANIFEST_UNKNOWN", "manifest unknown");
                    var bytes = await ReadAll(store, mf.StorageKey);
                    ctx.Response.Headers["Docker-Content-Digest"] = digest;
                    ctx.Response.ContentType = mf.ContentType;
                    if (ctx.Request.Method == "HEAD") { ctx.Response.Headers["Content-Length"] = bytes.Length.ToString(); return Results.StatusCode(200); }
                    return Results.Bytes(bytes, mf.ContentType);
                }

                if (ctx.Request.Method == "PUT")
                {
                    using var ms = new MemoryStream();
                    await ctx.Request.Body.CopyToAsync(ms, ct);
                    var bytes = ms.ToArray();
                    var digest = "sha256:" + PackageService.Sha256Hex(bytes);
                    var contentType = ctx.Request.ContentType ?? "application/vnd.oci.image.manifest.v1+json";
                    var key = await store.PutAsync(new MemoryStream(bytes), ct);
                    await pkgs.AddFileAsync(pkgId, null, digest, digest, bytes.Length, contentType, key, ct);

                    if (!reference.StartsWith("sha256:"))
                    {
                        await conn.ExecuteAsync("""
                            INSERT INTO oci_tags (package_id, tag, manifest_digest) VALUES (@pkgId, @reference, @digest)
                            ON CONFLICT (package_id, tag) DO UPDATE SET manifest_digest = EXCLUDED.manifest_digest, updated_at = now()
                            """, new { pkgId, reference, digest });
                        await pkgs.AddVersionAsync(pkgId, reference, "{}", uid!.Value, ct);
                    }
                    ctx.Response.Headers["Docker-Content-Digest"] = digest;
                    ctx.Response.Headers["Location"] = $"/v2/{orgSlug}/{imageName}/manifests/{reference}";
                    return Results.StatusCode(201);
                }
                return Results.StatusCode(405);
            }

            // ---- tags/list ----
            if (verb == "tags" && tail.Length >= 1 && tail[0] == "list")
            {
                var tags = (await conn.QueryAsync<string>("SELECT tag FROM oci_tags WHERE package_id = @pkgId ORDER BY tag", new { pkgId })).ToList();
                return Results.Json(new { name = $"{orgSlug}/{imageName}", tags });
            }

            return Results.NotFound();
        });
    }

    private static IResult DockerError(int status, string code, string message)
        => Results.Json(new { errors = new[] { new { code, message } } }, statusCode: status);

    private static async Task<byte[]> ReadAll(IArtifactStore store, string key)
    {
        var s = await store.OpenAsync(key) ?? throw new NotFoundException("missing");
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    private sealed record UploadRow(string Uuid, string StorageKey);
}
