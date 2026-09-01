using Dapper;
using GitSrv.Api.Data;

namespace GitSrv.Api.Ops;

public sealed record AuditEntry(long Id, long? OrgId, string ActorName, string Action, string Target, string Detail, string Ip, DateTime CreatedAt);

public sealed class AuditService(Db db)
{
    public async Task LogAsync(long? orgId, long? actorId, string actorName, string action, string target, string detail, string ip, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO audit_events (org_id, actor_id, actor_name, action, target, detail, ip)
            VALUES (@orgId, @actorId, @actorName, @action, @target, @detail, @ip)
            """, new { orgId, actorId, actorName = actorName ?? "", action, target = target ?? "", detail = detail ?? "", ip = ip ?? "" });
    }

    public async Task<IReadOnlyList<AuditEntry>> ForOrgAsync(long orgId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<AuditEntry>("""
            SELECT id, org_id AS OrgId, actor_name AS ActorName, action, target, detail, ip, created_at AS CreatedAt
            FROM audit_events WHERE org_id = @orgId ORDER BY created_at DESC LIMIT @limit
            """, new { orgId, limit })).ToList();
    }
}

/// <summary>Blocks webhook / integration URLs that resolve to loopback, link-local, private or
/// cloud-metadata addresses — the SSRF surface for user-supplied callback URLs.</summary>
public static class UrlGuard
{
    /// <summary>Set true only for local/QA where callback targets are on the compose network.</summary>
    public static bool AllowPrivateHosts { get; set; }

    public static void EnsureSafe(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            throw new Auth.ValidationException("Enter a valid http(s) URL.");
        if (AllowPrivateHosts) return;

        var host = u.DnsSafeHost;
        // Docker Desktop's host loopback alias — allowed so an operator can point a hook at a
        // service on the same host; it is not an arbitrary internal address.
        if (host == "host.docker.internal") return;
        if (host is "localhost" or "metadata.google.internal")
            throw new Auth.ValidationException("That host is not allowed.");

        // Resolve and reject private / loopback / link-local ranges.
        System.Net.IPAddress[] addrs;
        try { addrs = System.Net.Dns.GetHostAddresses(host); }
        catch { throw new Auth.ValidationException("Could not resolve that host."); }

        foreach (var ip in addrs)
        {
            if (System.Net.IPAddress.IsLoopback(ip)) throw new Auth.ValidationException("Loopback addresses are not allowed.");
            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                if (b[0] == 10 ||
                    (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                    (b[0] == 192 && b[1] == 168) ||
                    (b[0] == 169 && b[1] == 254) ||               // link-local / cloud metadata (169.254.169.254)
                    (b[0] == 127) || b[0] == 0)
                    throw new Auth.ValidationException("That address range is not allowed.");
            }
            else if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(System.Net.IPAddress.IPv6Loopback))
            {
                throw new Auth.ValidationException("That address range is not allowed.");
            }
        }
    }
}
