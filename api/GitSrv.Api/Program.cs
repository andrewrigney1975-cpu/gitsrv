using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Data;
using GitSrv.Api.Endpoints;
using GitSrv.Api.Http;
using GitSrv.Api.Identity;
using GitSrv.Api.Migrations;
using Npgsql;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

var isDev = builder.Environment.IsDevelopment();

if (!isDev)
{
    const string placeholderSigningKey = "dev-only-signing-key-change-me-please-32chars-min";
    var signingKey = builder.Configuration["Jwt:SigningKey"];
    if (string.IsNullOrWhiteSpace(signingKey) || signingKey == placeholderSigningKey || signingKey.Length < 32)
        throw new InvalidOperationException(
            "Jwt:SigningKey is missing, is the checked-in development placeholder, or is shorter than 32 characters.");

    var cs = builder.Configuration.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(cs) || cs.Contains("Password=change-me"))
        throw new InvalidOperationException("The database connection string is missing or still uses the dev password.");
}

var connString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

// Dapper: map snake_case columns to PascalCase members — and, via the custom map, to PascalCase
// constructor parameters too (positional records).
DefaultTypeMap.MatchNamesWithUnderscores = true;
UnderscoreConstructorTypeMap.Register(
    typeof(GitSrv.Api.Domain.User),
    typeof(GitSrv.Api.Domain.Organisation),
    typeof(GitSrv.Api.Domain.Team),
    typeof(GitSrv.Api.Domain.Repository),
    typeof(GitSrv.Api.Domain.SshKey),
    typeof(OrgSummary),
    typeof(OrgMember),
    typeof(TeamSummary),
    typeof(TeamMemberRow),
    typeof(RepoSummary));

builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connString).Build());
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton(new TokenOptions { SigningKey = builder.Configuration["Jwt:SigningKey"] ?? "" });
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<PasswordHasher>();

builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<Authorizer>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<OrgService>();
builder.Services.AddScoped<TeamService>();
builder.Services.AddScoped<RepoService>();
builder.Services.AddScoped<SshKeyService>();

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<CsrfMiddleware>();
app.UseMiddleware<AuthExtensions.CurrentUserMiddleware>();

if (builder.Configuration.GetValue("GitSrv:RunMigrationsOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<MigrationRunner>>();
    var sqlDir = Path.Combine(AppContext.BaseDirectory, "Migrations", "Sql");
    await new MigrationRunner(dataSource, sqlDir, logger).RunAsync();
}

app.MapGet("/health", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    try
    {
        await using var cmd = db.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
        return Results.Json(new { status = "ok", db = "ok" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "degraded", db = "unavailable", error = ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/meta", () => Results.Json(new
{
    name = "GitSrv",
    phase = 1,
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
}));

// Access-token cookies are marked Secure outside Development (behind the TLS-terminating proxy).
var cookiesSecure = !isDev;
app.MapAuth(cookiesSecure);
app.MapUsers();
app.MapOrgs();
app.MapRepos();

app.Run();

public partial class Program;
