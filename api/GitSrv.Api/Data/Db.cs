using System.Data;
using Npgsql;

namespace GitSrv.Api.Data;

/// <summary>Opens short-lived connections from the pooled <see cref="NpgsqlDataSource"/> for Dapper.</summary>
public sealed class Db(NpgsqlDataSource dataSource)
{
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
        => await dataSource.OpenConnectionAsync(ct);

    public async Task<T> InTransactionAsync<T>(Func<IDbConnection, IDbTransaction, Task<T>> work, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var result = await work(conn, tx);
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
