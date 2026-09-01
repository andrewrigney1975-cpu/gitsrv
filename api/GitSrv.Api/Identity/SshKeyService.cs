using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Data;
using GitSrv.Api.Domain;

namespace GitSrv.Api.Identity;

public sealed class SshKeyService(Db db)
{
    public async Task<IReadOnlyList<SshKey>> ListAsync(long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<SshKey>(
            """
            SELECT id, user_id, title, key_type, fingerprint, created_at, last_used_at
            FROM ssh_keys WHERE user_id = @userId ORDER BY created_at DESC
            """, new { userId })).ToList();
    }

    public async Task<SshKey> AddAsync(long userId, string title, string publicKey, CancellationToken ct)
    {
        if (!SshPublicKey.TryParse(publicKey, out var parsed, out var error))
            throw new ValidationException(error);

        title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(parsed) : title.Trim();
        if (title.Length > 100)
            throw new ValidationException("Key title is too long.");

        await using var conn = await db.OpenAsync(ct);
        if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM ssh_keys WHERE fingerprint = @fp)", new { fp = parsed.Fingerprint }))
            throw new ValidationException("That key is already registered.");

        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO ssh_keys (user_id, title, key_type, public_key, fingerprint)
            VALUES (@userId, @title, @keyType, @publicKey, @fingerprint) RETURNING id
            """,
            new { userId, title, keyType = parsed.KeyType, publicKey = parsed.NormalisedLine, fingerprint = parsed.Fingerprint });

        return new SshKey(id, userId, title, parsed.KeyType, parsed.Fingerprint, DateTime.UtcNow, null);
    }

    public async Task RemoveAsync(long userId, long keyId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var affected = await conn.ExecuteAsync("DELETE FROM ssh_keys WHERE id = @keyId AND user_id = @userId", new { keyId, userId });
        if (affected == 0)
            throw new NotFoundException("SSH key not found.");
    }

    private static string DefaultTitle(ParsedSshKey k)
    {
        var comment = k.NormalisedLine.Split(' ', 3);
        return comment.Length == 3 ? comment[2] : k.KeyType;
    }
}
