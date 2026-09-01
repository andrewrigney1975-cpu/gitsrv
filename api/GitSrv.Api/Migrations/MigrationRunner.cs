using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace GitSrv.Api.Migrations;

/// <summary>
/// Lightweight forward-only SQL migration runner. Applies every <c>*.sql</c> file in the
/// migrations directory in filename order, once each, inside a transaction, recording what ran
/// in <c>schema_migrations</c>. No down-migrations by design: roll forward.
/// </summary>
public sealed class MigrationRunner(NpgsqlDataSource dataSource, string sqlDirectory, ILogger<MigrationRunner> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        await EnsureHistoryTableAsync(ct);

        var applied = await LoadAppliedAsync(ct);

        var files = Directory.Exists(sqlDirectory)
            ? Directory.GetFiles(sqlDirectory, "*.sql").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList()
            : [];

        if (files.Count == 0)
        {
            logger.LogWarning("No migration files found in {Directory}", sqlDirectory);
            return;
        }

        foreach (var path in files)
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var sql = await File.ReadAllTextAsync(path, ct);
            var checksum = Sha256(sql);

            if (applied.TryGetValue(id, out var priorChecksum))
            {
                if (priorChecksum != checksum)
                {
                    throw new InvalidOperationException(
                        $"Migration '{id}' has already been applied but its contents have changed " +
                        $"(checksum {priorChecksum} -> {checksum}). Migrations are immutable once applied.");
                }
                continue;
            }

            logger.LogInformation("Applying migration {Id}", id);

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using (var cmd = new NpgsqlCommand(sql, conn, tx))
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var record = new NpgsqlCommand(
                    "INSERT INTO schema_migrations (id, checksum, applied_at) VALUES (@id, @checksum, now())",
                    conn, tx))
                {
                    record.Parameters.AddWithValue("id", id);
                    record.Parameters.AddWithValue("checksum", checksum);
                    await record.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        logger.LogInformation("Migrations up to date ({Count} file(s) known)", files.Count);
    }

    private async Task EnsureHistoryTableAsync(CancellationToken ct)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                id          text PRIMARY KEY,
                checksum    text NOT NULL,
                applied_at  timestamptz NOT NULL DEFAULT now()
            );
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<Dictionary<string, string>> LoadAppliedAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = dataSource.CreateCommand("SELECT id, checksum FROM schema_migrations");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    private static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));
        return Convert.ToHexStringLower(bytes);
    }
}
