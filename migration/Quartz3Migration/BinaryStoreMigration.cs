using System.Collections;
using System.Data;
using System.Data.Common;
using Quartz;
using Quartz.Impl.Calendar;
using Quartz.Simpl;
using Quartz.Spi;
using QuartzUpgradeTools;

namespace Quartz3Migration;

public sealed record ConversionReport(int BinaryBlobs, int JsonBlobs, int EmptyBlobs, bool Applied);

/// <summary>Converts only serialized columns; never starts a scheduler or recalculates a trigger.</summary>
public static class BinaryStoreMigration
{
    public static async Task<ConversionReport> ConvertAsync(DbConnection connection, string prefix,
        string? scheduler, bool apply, CancellationToken cancellationToken = default)
    {
        StoreSchema.ValidatePrefix(prefix);

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
            await StoreSchema.EnsureSchedulerHasRowsAsync(connection, transaction, prefix, scheduler, "no conversion was performed.", cancellationToken);
        foreach (var (suffix, column, keys) in StoreSchema.SerializedColumns)
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
        ValidateSupportedValue(value);
        var result = json.Serialize(value);
        var recovered = json.DeSerialize<T>(result) ?? throw new InvalidDataException("JSON round trip returned null.");
        if (value is IDictionary map && recovered is IDictionary recoveredMap)
            VerifyMap(map, recoveredMap);
        else if (value is not ICalendar calendar || recovered is not ICalendar recoveredCalendar
            || !SameCalendar(calendar, recoveredCalendar))
            throw new InvalidDataException("Calendar state changed during JSON conversion; use an application-specific migration.");
        return result;
    }

    // Default ordinal string comparers. .NET 10's default is an internal StringEqualityComparer, but binary
    // blobs written by earlier runtimes deserialize the same default as GenericEqualityComparer<string>.
    private static readonly HashSet<Type> DefaultStringComparerTypes = new[]
    {
        EqualityComparer<string>.Default.GetType(),
        StringComparer.Ordinal.GetType(),
        typeof(EqualityComparer<string>).Assembly.GetType("System.Collections.Generic.GenericEqualityComparer`1")?.MakeGenericType(typeof(string))
    }.OfType<Type>().ToHashSet();

    private static void ValidateSupportedValue(object value)
    {
        if (value is IDictionary map)
        {
            var supportedMap = value.GetType() == typeof(Dictionary<string, object>)
                && value is Dictionary<string, object> strings
                && DefaultStringComparerTypes.Contains(strings.Comparer.GetType())
                || value.GetType() == typeof(Dictionary<object, object>)
                && value is Dictionary<object, object> objects
                && objects.Comparer.GetType() == EqualityComparer<object>.Default.GetType();
            if (!supportedMap)
                throw new InvalidDataException("Custom map types or key comparers require application-specific migration.");
            foreach (DictionaryEntry entry in map)
            {
                if (entry.Key is not string || !IsSupportedScalar(entry.Value))
                    throw new InvalidDataException("Only string-keyed maps containing null, strings, booleans, integer values and byte arrays can be converted automatically. Other values require application-specific migration.");
            }
            return;
        }
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var calendar = value as ICalendar; calendar is not null; calendar = calendar.CalendarBase)
        {
            if (!visited.Add(calendar)) throw new InvalidDataException("Calendar base chain contains a cycle.");
            var type = calendar.GetType();
            if (type != typeof(BaseCalendar) && type != typeof(AnnualCalendar) && type != typeof(CronCalendar)
                && type != typeof(DailyCalendar) && type != typeof(HolidayCalendar)
                && type != typeof(MonthlyCalendar) && type != typeof(WeeklyCalendar))
                throw new InvalidDataException("Custom calendar types require application-specific migration.");
        }
    }

    private static bool IsInteger(object? value) => value is sbyte or byte or short or ushort or int or uint or long or ulong;

    private static bool SameCalendar(ICalendar? original, ICalendar? recovered)
    {
        if (original is null || recovered is null) return original is null && recovered is null;
        if (original.GetType() != recovered.GetType() || original.Description != recovered.Description
            || original is not BaseCalendar left || recovered is not BaseCalendar right
            || !SameTimeZone(left.TimeZone, right.TimeZone)
            || !SameCalendar(original.CalendarBase, recovered.CalendarBase)) return false;

        // Stock calendars only: compare their full scheduling settings, not serialization bookkeeping.
        var anchor = new DateTimeOffset(2000, 1, 15, 12, 0, 0, TimeSpan.Zero);
        return (left, right) switch
        {
            (AnnualCalendar a, AnnualCalendar b) => a.DaysExcluded.OrderBy(date => date).SequenceEqual(b.DaysExcluded.OrderBy(date => date)),
            (HolidayCalendar a, HolidayCalendar b) => a.ExcludedDates.OrderBy(date => date).SequenceEqual(b.ExcludedDates.OrderBy(date => date)),
            (MonthlyCalendar a, MonthlyCalendar b) => a.DaysExcluded.SequenceEqual(b.DaysExcluded),
            (WeeklyCalendar a, WeeklyCalendar b) => a.DaysExcluded.SequenceEqual(b.DaysExcluded),
            (CronCalendar a, CronCalendar b) => a.CronExpression.CronExpressionString == b.CronExpression.CronExpressionString
                && SameTimeZone(a.CronExpression.TimeZone, b.CronExpression.TimeZone),
            (DailyCalendar a, DailyCalendar b) => a.InvertTimeRange == b.InvertTimeRange
                && a.GetTimeRangeStartingTimeUtc(anchor) == b.GetTimeRangeStartingTimeUtc(anchor)
                && a.GetTimeRangeEndingTimeUtc(anchor) == b.GetTimeRangeEndingTimeUtc(anchor)
                && DailyPrecision(a) == DailyPrecision(b),
            _ => left.GetType() == typeof(BaseCalendar)
        };
    }

    private static bool SameTimeZone(TimeZoneInfo original, TimeZoneInfo recovered) =>
        original.Id == recovered.Id && original.HasSameRules(recovered);

    private static int DailyPrecision(DailyCalendar calendar)
    {
        // Quartz 3.14's binary constructor and JSON constructor can choose different next-included-time steps.
        // This private setting has no public getter. Fail closed if the pinned implementation changes.
        var field = typeof(DailyCalendar).GetField("precisionStepMillis",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(calendar) is int precision ? precision
            : throw new InvalidDataException("Cannot verify DailyCalendar precision; use application-specific migration.");
    }

    private static bool IsSupportedScalar(object? value) => value is null or string or bool or byte[] || IsInteger(value);

    private static void VerifyMap(IDictionary original, IDictionary recovered)
    {
        if (original.Count != recovered.Count)
            throw new InvalidDataException("JSON conversion changed the map's entries.");
        foreach (DictionaryEntry entry in original)
        {
            if (!recovered.Contains(entry.Key)) throw new InvalidDataException("JSON conversion removed a map entry.");
            var actual = recovered[entry.Key];
            var matches = entry.Value switch
            {
                byte[] bytes => actual is byte[] recoveredBytes && bytes.AsSpan().SequenceEqual(recoveredBytes),
                null => actual is null,
                _ when IsInteger(entry.Value) => IsInteger(actual) && Convert.ToDecimal(entry.Value) == Convert.ToDecimal(actual),
                _ => entry.Value.GetType() == actual?.GetType() && entry.Value.Equals(actual)
            };
            if (!matches) throw new InvalidDataException("JSON conversion changed a map value; use an application-specific migration.");
        }
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
