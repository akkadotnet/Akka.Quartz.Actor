using System.Data.Common;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace QuartzUpgradeTools;

/// <summary>
/// Store layout and option handling shared by the Quartz 3 converter and the Quartz 4 auditor, so the two
/// helpers always agree on which tables and rows they cover.
/// </summary>
internal static class StoreSchema
{
    public static readonly (string Suffix, string Column, string[] Keys)[] SerializedColumns =
    {
        ("JOB_DETAILS", "JOB_DATA", new[] { "JOB_NAME", "JOB_GROUP" }),
        ("TRIGGERS", "JOB_DATA", new[] { "TRIGGER_NAME", "TRIGGER_GROUP" }),
        ("CALENDARS", "CALENDAR", new[] { "CALENDAR_NAME" }),
        ("BLOB_TRIGGERS", "BLOB_DATA", new[] { "TRIGGER_NAME", "TRIGGER_GROUP" })
    };

    public static void ValidatePrefix(string prefix)
    {
        if (!Regex.IsMatch(prefix, "^[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)?$"))
            throw new ArgumentException("Table prefix must be an identifier, optionally qualified by one schema.", nameof(prefix));
    }

    /// <summary>Fails when --scheduler matches no stored rows, so a typo cannot produce a successful empty run.</summary>
    public static async Task EnsureSchedulerHasRowsAsync(DbConnection connection, DbTransaction? transaction, string prefix,
        string scheduler, string consequence, CancellationToken cancellationToken)
    {
        await using var scope = connection.CreateCommand();
        scope.Transaction = transaction;
        scope.CommandText = $"SELECT COUNT(*) FROM (SELECT SCHED_NAME FROM {prefix}JOB_DETAILS UNION ALL SELECT SCHED_NAME FROM {prefix}TRIGGERS UNION ALL SELECT SCHED_NAME FROM {prefix}CALENDARS) AS scoped WHERE SCHED_NAME = @scheduler";
        var parameter = scope.CreateParameter();
        parameter.ParameterName = "@scheduler";
        parameter.Value = scheduler;
        scope.Parameters.Add(parameter);
        if (Convert.ToInt64(await scope.ExecuteScalarAsync(cancellationToken)) == 0)
            throw new ArgumentException($"No stored rows match --scheduler. Verify the scheduler name; {consequence}");
    }

    /// <summary>Loads an application assembly, resolving its dependencies from the same directory.</summary>
    public static void LoadApplicationAssembly(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var dependency = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
        };
        AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
    }
}
