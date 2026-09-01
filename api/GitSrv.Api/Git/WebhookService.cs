using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Data;

namespace GitSrv.Api.Git;

public sealed record Webhook(long Id, string Url, string Events, string ContentType, bool IsActive, DateTime CreatedAt);
public sealed record HookDelivery(long Id, string Event, int? StatusCode, bool Ok, int DurationMs, string Error, DateTime CreatedAt);

public sealed class WebhookService(Db db, IHttpClientFactory http, ILogger<WebhookService> logger)
{
    public async Task<IReadOnlyList<Webhook>> ListAsync(long repoId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Webhook>("""
            SELECT id, url, events, content_type AS ContentType, is_active AS IsActive, created_at AS CreatedAt
            FROM repo_hooks WHERE repo_id = @repoId ORDER BY created_at
            """, new { repoId })).ToList();
    }

    public async Task<long> CreateAsync(long repoId, string url, string secret, string events, bool active, CancellationToken ct)
    {
        Ops.UrlGuard.EnsureSafe(url);
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO repo_hooks (repo_id, url, secret, events, is_active) VALUES (@repoId, @url, @secret, @events, @active) RETURNING id
            """, new { repoId, url, secret = secret ?? "", events = string.IsNullOrWhiteSpace(events) ? "push" : events, active });
    }

    public async Task DeleteAsync(long repoId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM repo_hooks WHERE id = @id AND repo_id = @repoId", new { id, repoId });
    }

    public async Task<IReadOnlyList<HookDelivery>> DeliveriesAsync(long repoId, long hookId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<HookDelivery>("""
            SELECT d.id, d.event, d.status_code AS StatusCode, d.ok, d.duration_ms AS DurationMs, d.error, d.created_at AS CreatedAt
            FROM hook_deliveries d JOIN repo_hooks h ON h.id = d.hook_id
            WHERE h.repo_id = @repoId AND d.hook_id = @hookId ORDER BY d.created_at DESC LIMIT 30
            """, new { repoId, hookId })).ToList();
    }

    /// <summary>Fire every active hook subscribed to <paramref name="evt"/>. Best-effort, records each delivery.</summary>
    public async Task DeliverAsync(long repoId, string evt, object payload, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var hooks = (await conn.QueryAsync<HookRow>(
            "SELECT id, url, secret, events FROM repo_hooks WHERE repo_id = @repoId AND is_active", new { repoId })).ToList();
        if (hooks.Count == 0) return;

        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var client = http.CreateClient("webhook");

        foreach (var h in hooks)
        {
            if (!h.Events.Split(',').Select(s => s.Trim()).Contains(evt)) continue;
            var sw = Stopwatch.StartNew();
            int? status = null;
            var ok = false;
            var error = "";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, h.Url)
                {
                    Content = new ByteArrayContent(body),
                };
                req.Content.Headers.ContentType = new("application/json");
                req.Headers.Add("X-GitSrv-Event", evt);
                req.Headers.Add("X-GitSrv-Delivery", Guid.NewGuid().ToString("N"));
                if (!string.IsNullOrEmpty(h.Secret))
                {
                    var sig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(h.Secret), body));
                    req.Headers.Add("X-GitSrv-Signature-256", "sha256=" + sig);
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var res = await client.SendAsync(req, cts.Token);
                status = (int)res.StatusCode;
                ok = res.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                logger.LogWarning(ex, "Webhook {HookId} delivery failed", h.Id);
            }
            sw.Stop();
            await conn.ExecuteAsync("""
                INSERT INTO hook_deliveries (hook_id, event, status_code, ok, duration_ms, error)
                VALUES (@id, @evt, @status, @ok, @ms, @error)
                """, new { h.Id, evt, status, ok, ms = (int)sw.ElapsedMilliseconds, error = Trim(error, 500) });
        }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    private sealed record HookRow(long Id, string Url, string Secret, string Events);
}
