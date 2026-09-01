using System.Security.Cryptography;
using System.Text;
using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Data;

namespace GitSrv.Api.Actions;

public sealed record SecretInfo(string Name, DateTime UpdatedAt);
file sealed record SecretRow(string Scope, string Name, string ValueEnc);

/// <summary>
/// Action secrets, encrypted at rest with AES-GCM under a key derived from config
/// (<c>GitSrv:SecretsKey</c>, falls back to the JWT signing key). Values are never returned to the
/// API surface — only decrypted server-side for a runner claim.
/// </summary>
public sealed class SecretsService(Db db, IConfiguration config)
{
    private readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes(
        config["GitSrv:SecretsKey"] ?? config["Jwt:SigningKey"] ?? "gitsrv-dev-secrets-key"));

    public async Task<IReadOnlyList<SecretInfo>> ListAsync(string scope, long ownerId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<SecretInfo>(
            "SELECT name, updated_at AS UpdatedAt FROM action_secrets WHERE scope = @scope AND owner_id = @ownerId ORDER BY name",
            new { scope, ownerId })).ToList();
    }

    public async Task SetAsync(string scope, long ownerId, string name, string value, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name ?? "", "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ValidationException("Secret names must be UPPER_SNAKE-ish (letters, digits, underscore; not starting with a digit).");
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO action_secrets (scope, owner_id, name, value_enc) VALUES (@scope, @ownerId, @name, @enc)
            ON CONFLICT (scope, owner_id, name) DO UPDATE SET value_enc = EXCLUDED.value_enc, updated_at = now()
            """, new { scope, ownerId, name, enc = Encrypt(value ?? "") });
    }

    public async Task DeleteAsync(string scope, long ownerId, string name, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM action_secrets WHERE scope = @scope AND owner_id = @ownerId AND name = @name",
            new { scope, ownerId, name });
    }

    /// <summary>Merged org + repo secrets (repo wins), decrypted. For the runner claim only.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveForJobAsync(long orgId, long repoId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<SecretRow>("""
            SELECT scope, name, value_enc AS ValueEnc FROM action_secrets
            WHERE (scope = 'org' AND owner_id = @orgId) OR (scope = 'repo' AND owner_id = @repoId)
            ORDER BY CASE scope WHEN 'org' THEN 0 ELSE 1 END
            """, new { orgId, repoId });
        var result = new Dictionary<string, string>();
        foreach (var r in rows) result[r.Name] = Decrypt(r.ValueEnc);
        return result;
    }

    private string Encrypt(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(plain);
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, pt, ct, tag);
        return Convert.ToBase64String(nonce) + "." + Convert.ToBase64String(ct) + "." + Convert.ToBase64String(tag);
    }

    private string Decrypt(string blob)
    {
        var parts = blob.Split('.');
        var nonce = Convert.FromBase64String(parts[0]);
        var ct = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var pt = new byte[ct.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, ct, tag, pt);
        return Encoding.UTF8.GetString(pt);
    }
}
