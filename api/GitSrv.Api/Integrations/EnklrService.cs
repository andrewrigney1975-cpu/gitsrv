using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Data;
using NotFoundException = GitSrv.Api.Auth.NotFoundException;

namespace GitSrv.Api.Integrations;

public sealed record EnklrConnection(long Id, long OrgId, string BaseUrl, string Workspace, string CardPrefix, bool IsActive);
public sealed record EnklrLink(string CardRef, string SourceKind, string SourceRef, string Title, string State, string Url, DateTime UpdatedAt);

/// <summary>
/// GitSrv's side of the Enklr project-management connector. Discovers <c>ENK-123</c> card
/// references in commits / branches / PRs, records the link, and pushes ref + lifecycle events to
/// the Enklr instance so a card can show linked work and move on merge. Enklr calls back into
/// <c>/api/integrations/enklr/...</c> (HMAC-verified) for the reverse direction.
///
/// Contract expected of the Enklr instance:
///   POST {base}/api/gitsrv/refs      { workspace, cardRef, kind, sourceRef, title, state, url }
///   POST {base}/api/gitsrv/events    { workspace, cardRef, type, sourceRef, url, actor }
/// Both authenticated with <c>Authorization: Bearer {api_token}</c>.
/// </summary>
public sealed class EnklrService(Db db, IHttpClientFactory http, ILogger<EnklrService> logger)
{
    public async Task<EnklrConnection?> GetForOrgAsync(long orgId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<EnklrConnection>("""
            SELECT id, org_id AS OrgId, base_url AS BaseUrl, workspace, card_prefix AS CardPrefix, is_active AS IsActive
            FROM enklr_connections WHERE org_id = @orgId
            """, new { orgId });
    }

    public async Task<EnklrConnection?> GetAsync(long id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<EnklrConnection>("""
            SELECT id, org_id AS OrgId, base_url AS BaseUrl, workspace, card_prefix AS CardPrefix, is_active AS IsActive
            FROM enklr_connections WHERE id = @id
            """, new { id });
    }

    public async Task<long> ConnectAsync(long orgId, long userId, string baseUrl, string workspace, string apiToken,
        string inboundSecret, string cardPrefix, CancellationToken ct)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _)) throw new ValidationException("Enter a valid Enklr base URL.");
        cardPrefix = string.IsNullOrWhiteSpace(cardPrefix) ? "ENK" : cardPrefix.Trim().ToUpperInvariant();
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO enklr_connections (org_id, base_url, workspace, api_token, inbound_secret, card_prefix, created_by)
            VALUES (@orgId, @baseUrl, @workspace, @apiToken, @inboundSecret, @cardPrefix, @userId)
            ON CONFLICT (org_id) DO UPDATE SET base_url = EXCLUDED.base_url, workspace = EXCLUDED.workspace,
                api_token = EXCLUDED.api_token, inbound_secret = EXCLUDED.inbound_secret, card_prefix = EXCLUDED.card_prefix,
                is_active = true
            RETURNING id
            """, new { orgId, baseUrl = baseUrl.TrimEnd('/'), workspace = workspace ?? "", apiToken = apiToken ?? "", inboundSecret = inboundSecret ?? "", cardPrefix, userId });
    }

    public async Task DisconnectAsync(long orgId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM enklr_connections WHERE org_id = @orgId", new { orgId });
    }

    public async Task<IReadOnlyList<EnklrLink>> LinksForCardAsync(long orgId, string cardRef, CancellationToken ct)
    {
        var c = await GetForOrgAsync(orgId, ct);
        if (c is null) return [];
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<EnklrLink>("""
            SELECT card_ref AS CardRef, source_kind AS SourceKind, source_ref AS SourceRef, title, state, url, updated_at AS UpdatedAt
            FROM enklr_links WHERE connection_id = @cid AND card_ref = @cardRef ORDER BY updated_at DESC
            """, new { cid = c.Id, cardRef })).ToList();
    }

    public async Task<IReadOnlyList<string>> LinkedCardsAsync(long connectionId, string sourceKind, string sourceRef, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<string>(
            "SELECT card_ref FROM enklr_links WHERE connection_id = @connectionId AND source_kind = @sourceKind AND source_ref = @sourceRef",
            new { connectionId, sourceKind, sourceRef })).ToList();
    }

    // ---- discovery + push ----

    private static IEnumerable<string> ExtractRefs(string prefix, string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in Regex.Matches(text, $@"\b{Regex.Escape(prefix)}-(\d+)\b", RegexOptions.IgnoreCase))
            yield return $"{prefix}-{m.Groups[1].Value}";
    }

    /// <summary>Called when a PR is opened/updated/merged/closed, or a branch/commit references a card.</summary>
    public async Task LinkAndNotifyAsync(long orgId, long repoId, string sourceKind, string sourceRef,
        string title, string state, string url, string text, string actor, string eventType, CancellationToken ct)
    {
        var c = await GetForOrgAsync(orgId, ct);
        if (c is null || !c.IsActive) return;

        var refs = ExtractRefs(c.CardPrefix, text).Distinct().ToList();
        if (refs.Count == 0) return;

        await using var conn = await db.OpenAsync(ct);
        foreach (var cardRef in refs)
        {
            await conn.ExecuteAsync("""
                INSERT INTO enklr_links (connection_id, repo_id, card_ref, source_kind, source_ref, title, state, url)
                VALUES (@cid, @repoId, @cardRef, @sourceKind, @sourceRef, @title, @state, @url)
                ON CONFLICT (connection_id, card_ref, source_kind, source_ref)
                DO UPDATE SET title = EXCLUDED.title, state = EXCLUDED.state, url = EXCLUDED.url, updated_at = now()
                """, new { cid = c.Id, repoId, cardRef, sourceKind, sourceRef, title, state, url });

            await PostAsync(c, "refs", new { workspace = c.Workspace, cardRef, kind = sourceKind, sourceRef, title, state, url }, ct);
            if (!string.IsNullOrEmpty(eventType))
                await PostAsync(c, "events", new { workspace = c.Workspace, cardRef, type = eventType, sourceRef, url, actor }, ct);
        }
    }

    /// <summary>Update the recorded state (e.g. CI status) for every link on a source, and push it.</summary>
    public async Task UpdateStateAsync(long orgId, string sourceKind, string sourceRef, string state, string url, CancellationToken ct)
    {
        var c = await GetForOrgAsync(orgId, ct);
        if (c is null || !c.IsActive) return;
        await using var conn = await db.OpenAsync(ct);
        var cards = (await conn.QueryAsync<string>(
            "SELECT card_ref FROM enklr_links WHERE connection_id = @cid AND source_kind = @sourceKind AND source_ref = @sourceRef",
            new { cid = c.Id, sourceKind, sourceRef })).ToList();
        if (cards.Count == 0) return;
        await conn.ExecuteAsync(
            "UPDATE enklr_links SET state = @state, url = COALESCE(NULLIF(@url,''), url), updated_at = now() WHERE connection_id = @cid AND source_kind = @sourceKind AND source_ref = @sourceRef",
            new { cid = c.Id, sourceKind, sourceRef, state, url });
        foreach (var cardRef in cards)
            await PostAsync(c, "refs", new { workspace = c.Workspace, cardRef, kind = sourceKind, sourceRef, state, url }, ct);
    }

    private async Task PostAsync(EnklrConnection c, string endpoint, object payload, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        int? status = null;
        var ok = false;
        var detail = "";
        try
        {
            var full = await conn.ExecuteScalarAsync<string>("SELECT api_token FROM enklr_connections WHERE id = @id", new { c.Id });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{c.BaseUrl}/api/gitsrv/{endpoint}")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(full)) req.Headers.Add("Authorization", $"Bearer {full}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var res = await http.CreateClient("enklr").SendAsync(req, cts.Token);
            status = (int)res.StatusCode;
            ok = res.IsSuccessStatusCode;
        }
        catch (Exception ex) { detail = ex.Message; logger.LogWarning(ex, "Enklr push {Endpoint} failed", endpoint); }
        await conn.ExecuteAsync(
            "INSERT INTO enklr_deliveries (connection_id, direction, event, status_code, ok, detail) VALUES (@id, 'out', @endpoint, @status, @ok, @detail)",
            new { c.Id, endpoint, status, ok, detail = detail.Length > 400 ? detail[..400] : detail });
    }

    // ---- inbound ----

    public async Task<bool> VerifyInboundAsync(long connectionId, string signature, byte[] body, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var secret = await conn.ExecuteScalarAsync<string>("SELECT inbound_secret FROM enklr_connections WHERE id = @connectionId", new { connectionId });
        if (string.IsNullOrEmpty(secret)) return false;
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature ?? ""));
    }

    public async Task RecordInboundAsync(long connectionId, string eventType, string detail, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO enklr_deliveries (connection_id, direction, event, ok, detail) VALUES (@connectionId, 'in', @eventType, true, @detail)",
            new { connectionId, eventType, detail = detail.Length > 400 ? detail[..400] : detail });
    }
}
