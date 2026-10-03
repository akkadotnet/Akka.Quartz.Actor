// The upgraded application: Akka.Quartz.Actor 1.5.71-beta1 and Quartz 4.0.1, reading the migrated SQLite store.
//
//   dotnet run -- <database> [seconds]

using System.Collections.Specialized;
using Akka.Actor;
using Akka.Quartz.Actor;
using Microsoft.Data.Sqlite;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: NewApp <database> [seconds]");
    return 1;
}

var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(args[0]), Pooling = false }.ToString();
var runFor = TimeSpan.FromSeconds(args.Length == 2 ? int.Parse(args[1]) : 12);

// Same scheduler name, table prefix and serializer family as before; the store type has its Quartz 4 name.
var properties = new NameValueCollection
{
    [QuartzActor.PropertySchedulerInstanceName] = "QuartzScheduler",
    ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.LocalTransactionJobStore, Quartz",
    ["quartz.jobStore.useProperties"] = "false",
    ["quartz.jobStore.dataSource"] = "default",
    ["quartz.jobStore.tablePrefix"] = "QRTZ_",
    ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.SQLiteDelegate, Quartz",
    ["quartz.dataSource.default.provider"] = "SQLite-Microsoft",
    ["quartz.dataSource.default.connectionString"] = connectionString,
    ["quartz.serializer.type"] = "newtonsoft"
};

// Saved jobs target akka://walkthrough/user/reminders, so the system name and actor path must not change.
var system = ActorSystem.Create("walkthrough", "akka.loglevel = WARNING\nakka.stdout-loglevel = WARNING");
system.ActorOf(Props.Create(() => new Reminders("Quartz 4")), "reminders");
system.ActorOf(Props.Create(() => new QuartzPersistentActor(properties)), "quartz");
Console.WriteLine($"Quartz 4 scheduler started; running for {runFor.TotalSeconds:0} seconds.");

await Task.Delay(runFor);
// Coordinated shutdown waits for the actor-owned scheduler, including running jobs.
await system.Terminate();
Console.WriteLine("Quartz 4 scheduler stopped.");
return 0;

sealed class Reminders : ReceiveActor
{
    public Reminders(string version) =>
        ReceiveAny(message => Console.WriteLine($"[{version}] {DateTime.Now:HH:mm:ss} {Self.Path.ToStringWithoutAddress()} received '{message}'"));
}
