using Dapper;
using GitSrv.Api.Data;
using GitSrv.Api.Domain;
using GitSrv.Api.Identity;

namespace GitSrv.Api.Auth;

public sealed record AuthResult(User User, string AccessJwt, DateTimeOffset AccessExpires, IssuedRefreshToken Refresh);

public sealed class AccountService(Db db, PasswordHasher hasher, TokenService tokens)
{
    public async Task<User> RegisterAsync(string username, string email, string displayName, string password, CancellationToken ct)
    {
        username = Slug.Normalise(username);
        email = email.Trim().ToLowerInvariant();

        if (!Slug.IsValid(username))
            throw new ValidationException("Username must be 1–40 chars, lowercase letters/digits with single - or _ separators, and not a reserved word.");
        if (!IsEmail(email))
            throw new ValidationException("Enter a valid email address.");
        if (password.Length < 10)
            throw new ValidationException("Password must be at least 10 characters.");
        if (displayName.Length > 100)
            throw new ValidationException("Display name is too long.");

        await using var conn = await db.OpenAsync(ct);

        if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM users WHERE username = @username)", new { username }))
            throw new ValidationException("That username is taken.");
        if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM users WHERE email = @email)", new { email }))
            throw new ValidationException("An account with that email already exists.");

        // The first account created becomes the site admin.
        var isFirst = !await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM users)");

        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, display_name, password_hash, is_site_admin)
            VALUES (@username, @email, @displayName, @hash, @isFirst)
            RETURNING id
            """,
            new { username, email, displayName = displayName.Trim(), hash = hasher.Hash(password), isFirst });

        return new User(id, username, email, displayName.Trim(), isFirst, DateTime.UtcNow);
    }

    private sealed record UserRow(long Id, string Username, string Email, string DisplayName, string PasswordHash, bool IsSiteAdmin, DateTime CreatedAt);
    private sealed record RefreshRow(long Id, long UserId, DateTime ExpiresAt, DateTime? RevokedAt);

    public async Task<AuthResult?> AuthenticateAsync(string usernameOrEmail, string password, string userAgent, CancellationToken ct)
    {
        var key = usernameOrEmail.Trim().ToLowerInvariant();
        await using var conn = await db.OpenAsync(ct);

        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            """
            SELECT id, username, email, display_name AS DisplayName, password_hash AS PasswordHash,
                   is_site_admin AS IsSiteAdmin, created_at AS CreatedAt
            FROM users WHERE username = @key OR email = @key
            """, new { key });

        if (row is null || !hasher.Verify(password, row.PasswordHash))
            return null;

        var user = new User(row.Id, row.Username, row.Email, row.DisplayName, row.IsSiteAdmin, row.CreatedAt);
        return await IssueSessionAsync(conn, user, userAgent, ct);
    }

    public async Task<AuthResult?> RefreshAsync(string refreshToken, string userAgent, CancellationToken ct)
    {
        var hash = TokenService.HashRefreshToken(refreshToken);
        return await db.InTransactionAsync(async (conn, tx) =>
        {
            var row = await conn.QuerySingleOrDefaultAsync<RefreshRow>(
                """
                SELECT id, user_id AS UserId, expires_at AS ExpiresAt, revoked_at AS RevokedAt
                FROM refresh_tokens WHERE token_hash = @hash FOR UPDATE
                """, new { hash }, tx);

            if (row is null || row.RevokedAt is not null || row.ExpiresAt <= DateTime.UtcNow)
                return (AuthResult?)null;

            // Rotate: revoke the presented token, issue a fresh pair.
            await conn.ExecuteAsync("UPDATE refresh_tokens SET revoked_at = now() WHERE id = @id", new { id = row.Id }, tx);

            var user = await conn.QuerySingleAsync<User>(
                "SELECT id, username, email, display_name, is_site_admin, created_at FROM users WHERE id = @id",
                new { id = row.UserId }, tx);

            return await IssueSessionAsync(conn, user, userAgent, ct, tx);
        }, ct);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        var hash = TokenService.HashRefreshToken(refreshToken);
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE refresh_tokens SET revoked_at = now() WHERE token_hash = @hash AND revoked_at IS NULL", new { hash });
    }

    public async Task<User?> GetAsync(long id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<User>(
            "SELECT id, username, email, display_name, is_site_admin, created_at FROM users WHERE id = @id",
            new { id });
    }

    public async Task UpdateProfileAsync(long id, string displayName, CancellationToken ct)
    {
        if (displayName.Trim().Length > 100)
            throw new ValidationException("Display name is too long.");
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE users SET display_name = @displayName, updated_at = now() WHERE id = @id",
            new { id, displayName = displayName.Trim() });
    }

    public async Task ChangePasswordAsync(long id, string current, string next, CancellationToken ct)
    {
        if (next.Length < 10)
            throw new ValidationException("New password must be at least 10 characters.");
        await using var conn = await db.OpenAsync(ct);
        var hash = await conn.ExecuteScalarAsync<string>("SELECT password_hash FROM users WHERE id = @id", new { id });
        if (hash is null || !hasher.Verify(current, hash))
            throw new ValidationException("Current password is incorrect.");
        await conn.ExecuteAsync("UPDATE users SET password_hash = @newHash, updated_at = now() WHERE id = @id",
            new { id, newHash = hasher.Hash(next) });

        // Any change of password ends every other session.
        await conn.ExecuteAsync("UPDATE refresh_tokens SET revoked_at = now() WHERE user_id = @id AND revoked_at IS NULL", new { id });
    }

    private async Task<AuthResult> IssueSessionAsync(System.Data.IDbConnection conn, User user, string userAgent, CancellationToken ct, System.Data.IDbTransaction? tx = null)
    {
        var (jwt, accessExpires) = tokens.IssueAccessToken(user.Id, user.Username, user.IsSiteAdmin);
        var refresh = tokens.IssueRefreshToken();
        await conn.ExecuteAsync(
            "INSERT INTO refresh_tokens (user_id, token_hash, expires_at, user_agent) VALUES (@userId, @hash, @expires, @ua)",
            new { userId = user.Id, hash = refresh.Hash, expires = refresh.ExpiresAt, ua = Trim(userAgent, 400) }, tx);
        return new AuthResult(user, jwt, accessExpires, refresh);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    private static bool IsEmail(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
}
