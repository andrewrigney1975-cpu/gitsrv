using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace GitSrv.Api.Auth;

public static class AuthExtensions
{
    /// <summary>
    /// Reads the access-token cookie, validates it, and populates the scoped <see cref="CurrentUser"/>.
    /// Invalid or absent tokens leave the request anonymous — endpoints enforce auth via
    /// <see cref="RequireAuth"/>.
    /// </summary>
    public sealed class CurrentUserMiddleware(RequestDelegate next)
    {
        public async Task Invoke(HttpContext ctx, CurrentUser currentUser, TokenService tokens)
        {
            var jwt = ctx.Request.Cookies[HttpAuth.AccessCookie];
            if (!string.IsNullOrEmpty(jwt))
            {
                try
                {
                    var handler = new JwtSecurityTokenHandler();
                    var principal = handler.ValidateToken(jwt, tokens.ValidationParameters, out _);
                    currentUser.SetFrom(principal);
                }
                catch (SecurityTokenException)
                {
                    // stale/invalid access cookie — stay anonymous, client will hit /api/auth/refresh
                }
            }

            await next(ctx);
        }
    }

    public static TBuilder RequireAuth<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, nextFilter) =>
        {
            var cu = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            if (!cu.IsAuthenticated)
                throw new UnauthorizedException();
            return await nextFilter(ctx);
        });
        return builder;
    }
}
