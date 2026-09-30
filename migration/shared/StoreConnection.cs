using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace QuartzUpgradeTools;

internal static class StoreConnection
{
    public static DbConnection Create(string provider, string? database, string? connectionStringEnvironment, bool readOnly)
    {
        if (provider == "sqlite")
        {
            if (connectionStringEnvironment is not null)
                throw new ArgumentException("SQLite uses --database, not --connection-string-env.");
            if (database is null || !File.Exists(database))
                throw new ArgumentException("An existing --database file is required.");
            return new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(database),
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite
            }.ToString());
        }
        if (provider is not ("sqlserver" or "postgres"))
            throw new ArgumentException("--provider must be sqlite, sqlserver or postgres.");
        if (database is not null)
            throw new ArgumentException("SQL Server/PostgreSQL use --connection-string-env, not --database.");
        var connectionString = connectionStringEnvironment is null ? null : Environment.GetEnvironmentVariable(connectionStringEnvironment);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("--connection-string-env must name an environment variable containing the connection string.");
        if (provider == "sqlserver") return new SqlConnection(connectionString);
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (readOnly) settings.Options = (settings.Options + " -c default_transaction_read_only=on").Trim();
        return new NpgsqlConnection(settings.ToString());
    }
}
