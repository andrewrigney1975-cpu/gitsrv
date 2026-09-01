using System.Security.Cryptography;
using System.Text;
using Dapper;
using GitSrv.Api.Data;

namespace GitSrv.Api.Auth;

public sealed record PatSummary(long Id, string Name, string TokenPrefix, bool ScopeRead, bool ScopeWrite,
    DateTime CreatedAt, DateTime? LastUsedAt, DateTime? ExpiresAt);

public sealed record PatVerification(long UserId, bool ScopeRead, bool ScopeWrite);

/// <summary>
/// Personal access tokens. Format <c>gsp_&lt;64 hex&gt;</c>; only the SHA-256 hash is stored. Used
/// as the HTTP Basic password for git, and later for the API.
/// </summary>
public sealed class PatService(Db db)
{
    public const string Prefix = "gsp_";

    public async Task<IReadOnlyList<PatSummary>> ListAsync(long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<PatSummary>(
            """
            SELECT id, name, token_prefix AS TokenPrefix, scope_read AS ScopeRead, scope_write AS ScopeWrite,
                   created_at AS CreatedAt, last_used_at AS LastUsedAt, expires_at AS ExpiresAt
            FROM personal_access_tokens WHERE user_id = @userId ORDER BY created_at DESC
            """, new { userId })).ToList();
    }

    public async Task<(PatSummary Summary, string Token)> CreateAsync(long userId, string name,
        bool scopeRead, bool scopeWrite, DateTime? expiresAt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 60)
            throw new ValidationException("Token name is required and must be 60 chars or fewer.");
        if (!scopeRead && !scopeWrite)
            throw new ValidationException("A token needs at least one scope.");

        var token = Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(token);
        var display = token[..12];

        await using var conn = await db.OpenAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO personal_access_tokens (user_id, name, token_hash, token_prefix, scope_read, scope_write, expires_at)
            VALUES (@userId, @name, @hash, @display, @scopeRead, @scopeWrite, @expiresAt)
            RETURNING id
            """, new { userId, name = name.Trim(), hash, display, scopeRead, scopeWrite, expiresAt });

        return (new PatSummary(id, name.Trim(), display, scopeRead, scopeWrite, DateTime.UtcNow, null, expiresAt), token);
    }

    public async Task RevokeAsync(long userId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var n = await conn.ExecuteAsync("DELETE FROM personal_access_tokens WHERE id = @id AND user_id = @userId", new { id, userId });
        if (n == 0) throw new NotFoundException("Token not found.");
    }

    /// <summary>Returns the owning user and scopes if the token is valid and unexpired.</summary>
    public async Task<PatVerification?> VerifyAsync(string token, CancellationToken ct)
    {
        if (!token.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<PatVerification>(
            """
            SELECT user_id AS UserId, scope_read AS ScopeRead, scope_write AS ScopeWrite
            FROM personal_access_tokens
            WHERE token_hash = @hash AND (expires_at IS NULL OR expires_at > now())
            """, new { hash = Hash(token) });

        if (row is not null)
            await conn.ExecuteAsync("UPDATE personal_access_tokens SET last_used_at = now() WHERE token_hash = @hash", new { hash = Hash(token) });

        return row;
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
