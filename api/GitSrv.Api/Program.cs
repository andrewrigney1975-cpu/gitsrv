using System.Text.Json;
using GitSrv.Api.Migrations;
using Npgsql;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logs to stdout, captured via `docker compose logs api`. Every ILogger<T>
// injection routes through this sink.
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

// Defence-in-depth against the checked-in dev placeholders reaching a real deployment. Development
// is exempt so a zero-setup local run keeps working.
if (!builder.Environment.IsDevelopment())
{
    const string placeholderSigningKey = "dev-only-signing-key-change-me-please-32chars-min";
    var signingKey = builder.Configuration["Jwt:SigningKey"];
    if (string.IsNullOrWhiteSpace(signingKey) || signingKey == placeholderSigningKey || signingKey.Length < 32)
    {
        throw new InvalidOperationException(
            "Jwt:SigningKey is missing, is the checked-in development placeholder, or is shorter " +
            "than 32 characters. Set a real, random JWT_SIGNING_KEY before starting outside Development.");
    }

    var connectionString = builder.Configuration.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("Password=change-me"))
    {
        throw new InvalidOperationException(
            "The database connection string is missing or still uses the checked-in development " +
            "password. Set a real DB_PASSWORD before starting outside Development.");
    }
}

var connString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connString).Build());
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseSerilogRequestLogging();

// Run pending SQL migrations before serving traffic.
if (builder.Configuration.GetValue("GitSrv:RunMigrationsOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<MigrationRunner>>();
    var sqlDir = Path.Combine(AppContext.BaseDirectory, "Migrations", "Sql");
    await new MigrationRunner(dataSource, sqlDir, logger).RunAsync();
}

// Liveness + readiness. `web` and the compose healthcheck both hit this.
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

// Minimal identity of the running build, useful for the front-end skeleton and smoke tests.
app.MapGet("/api/meta", () => Results.Json(new
{
    name = "GitSrv",
    phase = 0,
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
}));

app.Run();

public partial class Program;
