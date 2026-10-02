using System.Data.Common;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl;
using QuartzUpgradeTools;

namespace Akka.Quartz.Actor.Upgrade;

public sealed record AuditIssue(string Location, string Reason);
public sealed record AuditReport(int CronTriggers, int Blobs, IReadOnlyList<AuditIssue> Issues);

/// <summary>Read-only checks against the pinned Quartz 4 parser and Newtonsoft reader, before schema migration.</summary>
public static class StoreAudit
{
    public static async Task<AuditReport> InspectAsync(DbConnection connection, string prefix = "QRTZ_",
        string? scheduler = null, CancellationToken cancellationToken = default)
    {
        StoreSchema.ValidatePrefix(prefix);
        if (scheduler is not null)
            await StoreSchema.EnsureSchedulerHasRowsAsync(connection, null, prefix, scheduler, "this is not a successful audit.", cancellationToken);
        var issues = new List<AuditIssue>();
        var cronCount = 0;
        var blobCount = 0;
        await using var cron = connection.CreateCommand();
        cron.CommandText = $"SELECT SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP, CRON_EXPRESSION, TIME_ZONE_ID FROM {prefix}CRON_TRIGGERS";
        Scope(cron, scheduler);
        await using (var reader = await cron.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                cronCount++;
                var location = $"cron {reader.GetString(0)}/{reader.GetString(2)}/{reader.GetString(1)}";
                var expression = reader.GetString(3);
                // Any parser failure is a finding for this row; it must not abort the rest of the audit.
                try { _ = new CronExpression(expression); }
                catch (Exception) { issues.Add(new AuditIssue(location, $"Quartz 4.0.1 rejects '{expression}'; choose the intended replacement schedule.")); }
                if (!reader.IsDBNull(4))
                {
                    // Resolve the way Quartz 4 does at runtime, including its alias fallbacks.
                    try { _ = TimeZones.FindById(reader.GetString(4)); }
                    catch (InvalidTimeZoneException) { issues.Add(new AuditIssue(location, "Time zone is invalid on this host.")); }
                    catch (Exception) { issues.Add(new AuditIssue(location, "Quartz 4 cannot resolve this time zone on this host.")); }
                }
            }
        }
        var serializer = new NewtonsoftJsonObjectSerializer();
        foreach (var (suffix, column, keys) in StoreSchema.SerializedColumns)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT SCHED_NAME, {string.Join(", ", keys)}, {column} FROM {prefix}{suffix}";
            Scope(command, scheduler);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(keys.Length + 1)) continue;
                var bytes = (byte[])reader.GetValue(keys.Length + 1);
                if (bytes.Length == 0) continue;
                blobCount++;
                var location = $"{suffix} {string.Join('/', Enumerable.Range(0, keys.Length + 1).Select(reader.GetString))}";
                if (suffix == "BLOB_TRIGGERS")
                {
                    issues.Add(new AuditIssue(location, "Custom BLOB trigger: requires application-specific serializer registration and migration rehearsal."));
                    continue;
                }
                if (bytes[0] == 0)
                {
                    issues.Add(new AuditIssue(location, "Binary blob: convert with the Quartz 3 helper before upgrading."));
                    continue;
                }
                try
                {
                    object? recovered = suffix switch
                    {
                        "CALENDARS" => serializer.Deserialize<ICalendar>(bytes),
                        "BLOB_TRIGGERS" => serializer.Deserialize<IOperableTrigger>(bytes),
                        _ => serializer.Deserialize<JobDataMap>(bytes)
                    };
                    if (recovered is null) issues.Add(new AuditIssue(location, "Nonempty JSON blob deserialized to null."));
                }
                catch (Exception)
                {
                    // Serializer messages can contain the payload. Report identity, never message contents.
                    issues.Add(new AuditIssue(location, "Quartz 4 Newtonsoft cannot read this blob; check custom types, calendar cron expressions and serializer registration."));
                }
            }
        }
        return new AuditReport(cronCount, blobCount, issues);
    }

    private static void Scope(DbCommand command, string? scheduler)
    {
        if (scheduler is null) return;
        command.CommandText += " WHERE SCHED_NAME = @scheduler";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@scheduler";
        parameter.Value = scheduler;
        command.Parameters.Add(parameter);
    }
}
