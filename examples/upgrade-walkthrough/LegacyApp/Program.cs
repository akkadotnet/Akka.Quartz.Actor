// An application on Akka.Quartz.Actor 1.5.59 and Quartz 3.14, storing jobs in SQLite with the binary serializer.
//
//   dotnet run -- seed <database> [binary|json]   create the Quartz 3 tables, schedule sample jobs and run them briefly
//   dotnet run -- fix-cron <database> [binary|json]   replace a schedule that Quartz 4 rejects, while Quartz 3 still can
//
// binary (the default) is BinaryFormatter storage, which must be converted; json is Quartz 3's Newtonsoft serializer.

using System.Collections.Specialized;
using Akka.Actor;
using Akka.Quartz.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Microsoft.Data.Sqlite;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Calendar;
using Quartz.Impl.Matchers;

if (args.Length is < 2 or > 3 || args[0] is not ("seed" or "fix-cron") || args.Length == 3 && args[2] is not ("binary" or "json"))
{
    Console.Error.WriteLine("Usage: LegacyApp seed|fix-cron <database> [binary|json]");
    return 1;
}
var serializer = args.Length == 3 ? args[2] : "binary";

var database = Path.GetFullPath(args[1]);
var connectionString = new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString();

if (args[0] == "seed")
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var schema = connection.CreateCommand();
    schema.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "tables_sqlite_quartz3.sql"));
    await schema.ExecuteNonQueryAsync();
}

// The Quartz 3 configuration an existing application would have had.
var properties = new NameValueCollection
{
    ["quartz.scheduler.instanceName"] = "QuartzScheduler",
    ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.JobStoreTX, Quartz",
    ["quartz.jobStore.useProperties"] = "false",
    ["quartz.jobStore.dataSource"] = "default",
    ["quartz.jobStore.tablePrefix"] = "QRTZ_",
    ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.SQLiteDelegate, Quartz",
    ["quartz.dataSource.default.provider"] = "SQLite-Microsoft",
    ["quartz.dataSource.default.connectionString"] = connectionString,
    ["quartz.serializer.type"] = serializer
};
var scheduler = await new StdSchedulerFactory(properties).GetScheduler();

if (args[0] == "fix-cron")
{
    // Quartz 4.0.1 rejects MON/2. In Quartz 3 it fires every other Monday, which no cron expression can express,
    // so continue the same sequence with a two-week calendar interval starting at the next scheduled fire time.
    var key = new TriggerKey("inventory-sync", "reports");
    var existing = (ICronTrigger)(await scheduler.GetTrigger(key) ?? throw new InvalidOperationException("Trigger not found."));
    var nextFire = existing.GetNextFireTimeUtc() ?? throw new InvalidOperationException("Trigger has no next fire time.");
    var replacement = TriggerBuilder.Create()
        .WithIdentity(key)
        .ForJob(existing.JobKey)
        .StartAt(nextFire)
        .WithCalendarIntervalSchedule(s => s
            .WithIntervalInWeeks(2)
            .InTimeZone(existing.TimeZone)
            .PreserveHourOfDayAcrossDaylightSavings(true))
        .Build();
    await scheduler.RescheduleJob(key, replacement);
    var local = TimeZoneInfo.ConvertTime(nextFire, existing.TimeZone);
    Console.WriteLine($"Rescheduled {key}: cron '{existing.CronExpressionString}' -> every 2 weeks from {local:ddd yyyy-MM-dd HH:mm} {existing.TimeZone.Id}");
    await scheduler.Shutdown();
    return 0;
}

var holidays = new HolidayCalendar();
holidays.AddExcludedDate(new DateTime(2026, 12, 25));
await scheduler.AddCalendar("holidays", holidays, replace: true, updateTriggers: false);

var system = ActorSystem.Create("walkthrough", "akka.loglevel = WARNING\nakka.stdout-loglevel = WARNING");
var reminders = system.ActorOf(Props.Create(() => new Reminders("Quartz 3")), "reminders");
var quartz = system.ActorOf(Props.Create(() => new QuartzPersistentActor(scheduler)), "quartz");

ITrigger[] triggers =
{
    TriggerBuilder.Create().WithIdentity("heartbeat", "monitoring").ForJob("heartbeat", "monitoring")
        .StartNow().WithSimpleSchedule(s => s.WithIntervalInSeconds(5).RepeatForever()).Build(),
    TriggerBuilder.Create().WithIdentity("daily-report", "reports").ForJob("daily-report", "reports")
        .WithCronSchedule("0 0 6 * * ?").ModifiedByCalendar("holidays").Build(),
    TriggerBuilder.Create().WithIdentity("inventory-sync", "reports").ForJob("inventory-sync", "reports")
        .WithCronSchedule("0 0 9 ? * MON/2").Build(),
    TriggerBuilder.Create().WithIdentity("weekly-cleanup", "maintenance").ForJob("weekly-cleanup", "maintenance")
        .WithCronSchedule("0 0 3 ? * SUN").Build()
};
string[] messages = { "heartbeat", "send-daily-report", "sync-inventory", "run-cleanup" };
for (var index = 0; index < triggers.Length; index++)
{
    var created = await quartz.Ask(new CreatePersistentJob(reminders.Path, messages[index], triggers[index]), TimeSpan.FromSeconds(10));
    Console.WriteLine(created is JobCreated job ? $"Scheduled {job.JobKey} ({triggers[index].Key})" : $"Failed: {created}");
}

// 1.5.59 does not await ScheduleJob, so give the writes a moment before pausing and starting.
await Task.Delay(TimeSpan.FromSeconds(1));
await scheduler.PauseTriggers(GroupMatcher<TriggerKey>.GroupEquals("maintenance"));
Console.WriteLine("Paused trigger group 'maintenance'");

await scheduler.Start();
await Task.Delay(TimeSpan.FromSeconds(11));
await scheduler.Shutdown(waitForJobsToComplete: true);
await system.Terminate();
Console.WriteLine("Quartz 3 scheduler stopped.");
return 0;

sealed class Reminders : ReceiveActor
{
    public Reminders(string version) =>
        ReceiveAny(message => Console.WriteLine($"[{version}] {DateTime.Now:HH:mm:ss} {Self.Path.ToStringWithoutAddress()} received '{message}'"));
}
