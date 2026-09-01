using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace GitSrv.Api.Auth;

public sealed class TokenOptions
{
    public string SigningKey { get; init; } = "";
    public string Issuer { get; init; } = "gitsrv";
    public TimeSpan AccessLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshLifetime { get; init; } = TimeSpan.FromDays(30);
}

public sealed record IssuedRefreshToken(string Token, string Hash, DateTimeOffset ExpiresAt);

/// <summary>
/// Mints the short-lived access JWT (carried in an HttpOnly cookie) and opaque refresh tokens
/// (also HttpOnly; only their SHA-256 hash is stored, in <c>refresh_tokens</c>).
/// </summary>
public sealed class TokenService(TokenOptions options)
{
    private readonly SymmetricSecurityKey _key = new(Encoding.UTF8.GetBytes(options.SigningKey));

    public (string Jwt, DateTimeOffset ExpiresAt) IssueAccessToken(long userId, string username, bool isSiteAdmin)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now + options.AccessLifetime;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new("username", username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (isSiteAdmin)
            claims.Add(new Claim("site_admin", "true"));

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Issuer,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public IssuedRefreshToken IssueRefreshToken()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexStringLower(raw);
        return new IssuedRefreshToken(token, HashRefreshToken(token), DateTimeOffset.UtcNow + options.RefreshLifetime);
    }

    public static string HashRefreshToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Issuer,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _key,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
    };
}
