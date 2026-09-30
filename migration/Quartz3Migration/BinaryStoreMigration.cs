using System.Collections;
using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Quartz;
using Quartz.Simpl;
using Quartz.Spi;

namespace Quartz3Migration;

public sealed record ConversionReport(int BinaryBlobs, int JsonBlobs, int EmptyBlobs, bool Applied);

/// <summary>Converts only serialized columns; never starts a scheduler or recalculates a trigger.</summary>
public static class BinaryStoreMigration
{
    public static async Task<ConversionReport> ConvertAsync(DbConnection connection, string prefix,
        string? scheduler, bool apply, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(prefix, "^[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)?$"))
            throw new ArgumentException("Table prefix must be an identifier, optionally qualified by one schema.", nameof(prefix));

        var binary = new BinaryObjectSerializer();
        binary.Initialize();
        var json = new JsonObjectSerializer();
        json.Initialize();
        var binaryCount = 0;
        var jsonCount = 0;
        var emptyCount = 0;
        // Offline operation is required. The transaction also prevents a failed conversion from partially committing.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (scheduler is not null)
        {
            await using var scope = connection.CreateCommand();
            scope.Transaction = transaction;
            scope.CommandText = $"SELECT COUNT(*) FROM (SELECT SCHED_NAME FROM {prefix}JOB_DETAILS UNION ALL SELECT SCHED_NAME FROM {prefix}TRIGGERS UNION ALL SELECT SCHED_NAME FROM {prefix}CALENDARS) AS scoped WHERE SCHED_NAME = @scheduler";
            AddParameter(scope, "@scheduler", scheduler);
            if (Convert.ToInt64(await scope.ExecuteScalarAsync(cancellationToken)) == 0)
                throw new ArgumentException("No stored rows match --scheduler. Verify the scheduler name; no conversion was performed.");
        }
        foreach (var (suffix, column, keys) in new[]
        {
            ("JOB_DETAILS", "JOB_DATA", new[] { "JOB_NAME", "JOB_GROUP" }),
            ("TRIGGERS", "JOB_DATA", new[] { "TRIGGER_NAME", "TRIGGER_GROUP" }),
            ("CALENDARS", "CALENDAR", new[] { "CALENDAR_NAME" }),
            ("BLOB_TRIGGERS", "BLOB_DATA", new[] { "TRIGGER_NAME", "TRIGGER_GROUP" })
        })
        {
            var table = prefix + suffix;
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = $"SELECT SCHED_NAME, {string.Join(", ", keys)}, {column} FROM {table}"
                + (scheduler is null ? "" : " WHERE SCHED_NAME = @scheduler");
            if (scheduler is not null) AddParameter(select, "@scheduler", scheduler);
            var rows = new List<(string[] Identity, byte[] Bytes)>();
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.IsDBNull(keys.Length + 1)) { emptyCount++; continue; }
                    var bytes = (byte[])reader.GetValue(keys.Length + 1);
                    if (bytes.Length == 0) { emptyCount++; continue; }
                    var identity = Enumerable.Range(0, keys.Length + 1).Select(reader.GetString).ToArray();
                    rows.Add((identity, bytes));
                }
            }

            foreach (var (identity, original) in rows)
            {
                if (suffix == "BLOB_TRIGGERS")
                    throw new InvalidDataException($"Custom BLOB trigger {string.Join('/', identity)} requires an application-specific serializer and migration; no transaction committed.");
                byte[] replacement;
                var isBinary = original[0] == 0; // BinaryFormatter's SerializedStreamHeader record.
                try
                {
                    replacement = suffix switch
                    {
                        "CALENDARS" => ConvertBlob<ICalendar>(original, isBinary, binary, json),
                        _ => ConvertBlob<IDictionary>(original, isBinary, binary, json)
                    };
                }
                catch (Exception exception)
                {
                    // Do not print serializer exceptions containing payload data. No writes are committed on failure.
                    throw new InvalidDataException($"Cannot convert {table} {string.Join('/', identity)}."
                        + " Check the blob format and required application assemblies/custom serializers.", exception);
                }
                if (!isBinary) { jsonCount++; continue; }
                binaryCount++;
                if (!apply) continue;

                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                var identityColumns = new[] { "SCHED_NAME" }.Concat(keys).ToArray();
                update.CommandText = $"UPDATE {table} SET {column} = @replacement WHERE "
                    + string.Join(" AND ", identityColumns.Select((name, index) => $"{name} = @key{index}"))
                    + $" AND {column} = @original";
                for (var index = 0; index < identity.Length; index++) AddParameter(update, $"@key{index}", identity[index]);
                AddParameter(update, "@replacement", replacement, DbType.Binary);
                AddParameter(update, "@original", original, DbType.Binary);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException($"{table} changed during conversion; stop every scheduler and retry from a backup.");
            }
        }
        if (apply) await transaction.CommitAsync(cancellationToken);
        else await transaction.RollbackAsync(cancellationToken);
        return new ConversionReport(binaryCount, jsonCount, emptyCount, apply);
    }

    private static byte[] ConvertBlob<T>(byte[] original, bool isBinary, BinaryObjectSerializer binary,
        JsonObjectSerializer json) where T : class
    {
        var value = (isBinary ? binary.DeSerialize<T>(original) : json.DeSerialize<T>(original))
            ?? throw new InvalidDataException("A nonempty blob deserialized to null.");
        if (!isBinary) return original;
        var result = json.Serialize(value);
        var recovered = json.DeSerialize<T>(result) ?? throw new InvalidDataException("JSON round trip returned null.");
        if (!result.AsSpan().SequenceEqual(json.Serialize(recovered)))
            throw new InvalidDataException("JSON round trip changed the serialized value.");
        return result;
    }

    private static void AddParameter(DbCommand command, string name, object value, DbType? type = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        if (type is { } dbType) parameter.DbType = dbType;
        command.Parameters.Add(parameter);
    }
}
