using Npgsql;

namespace ITrade.Infrastructure.Persistence;

/// <summary>
/// One running Host per database. Two would each run the daily jobs — and, later, each send the same order.
/// The lock is a PostgreSQL advisory lock held by a connection of its own for as long as the Host lives;
/// the server releases it by itself if the process dies.
/// </summary>
public sealed class InstanceLock(string connectionString) : IAsyncDisposable
{
    /// <summary>"iTrade" in ASCII, then 01: the key of this lock among the database's advisory locks.</summary>
    private const long Key = 0x6954_7261_6465_0001;

    private NpgsqlConnection? _connection;

    /// <summary>Takes the lock or throws when another Host holds it.</summary>
    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return;
        }

        // Not from the pool: closing a pooled connection would leave the lock on the idle physical connection.
        var dedicated = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        var connection = new NpgsqlConnection(dedicated);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", Key);
            if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new InvalidOperationException(
                    "Another iTrade Host is already running against this database. Stop it first: " +
                    "two Hosts would run the same jobs and, later, send the same orders.");
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        _connection = connection;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is { } connection)
        {
            _connection = null;
            await connection.DisposeAsync();
        }
    }
}
