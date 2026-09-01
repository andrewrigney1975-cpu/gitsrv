using GitSrv.Api.Auth;

namespace GitSrv.Api.Http;

/// <summary>Maps the domain exception types to HTTP responses with a consistent JSON body.</summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task Invoke(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (Exception ex)
        {
            var (status, message) = ex switch
            {
                UnauthorizedException => (StatusCodes.Status401Unauthorized, ex.Message),
                ForbiddenException => (StatusCodes.Status403Forbidden, ex.Message),
                NotFoundException => (StatusCodes.Status404NotFound, ex.Message),
                ValidationException => (StatusCodes.Status422UnprocessableEntity, ex.Message),
                BadHttpRequestException => (StatusCodes.Status400BadRequest, "Malformed request."),
                _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
            };

            if (status == StatusCodes.Status500InternalServerError)
                logger.LogError(ex, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);

            if (ctx.Response.HasStarted)
                throw;

            ctx.Response.Clear();
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(new { error = message });
        }
    }
}
