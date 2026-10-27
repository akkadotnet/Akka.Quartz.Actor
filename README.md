This is the Quartz integration plugin for Akka.NET.



## Upgrading to Quartz 4

Akka.Quartz.Actor **1.5.71-beta2** aligns with **Akka.NET 1.5.71** and requires **.NET 10 / Quartz 4.0.1**. This is a breaking upgrade; pin **1.5.59** if you need the previous platform support.

Read the [upgrade guide](https://github.com/akkadotnet/Akka.Quartz.Actor/blob/dev/docs/upgrading-to-quartz4.md) before updating an existing deployment (also included in the NuGet package as `docs/upgrading-to-quartz4.md`). It covers offline cutover, binary storage conversion, cron auditing, mandatory schema migration, serializer configuration, startup ordering and rollback. Standalone SQL Server, PostgreSQL and SQLite conversion/audit helpers are attached to each [GitHub release](https://github.com/akkadotnet/Akka.Quartz.Actor/releases) as `quartz-upgrade-tools.zip`; or build them from a checkout of the release tag; see the guide.

## Using ##
Install:
```
PM>Install-Package Akka.Quartz.Actor
```
Create a Receiver:
```csharp
class Receiver: ActorBase
{
    public Receiver()
    {
    }

    protected override bool Receive(object message)
    {
    	//handle scheduled message here
    }
 }
var receiver = Sys.ActorOf(Props.Create(() => new Receiver()), "Receiver");
```

Create a QuartzActor:
```csharp
var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
```

Send "Hello" message to Receiver Actor:
```csharp
quartzActor.Tell(new CreateJob(receiver, "Hello", TriggerBuilder.Create().WithCronSchedule( " * * * * * ?").Build())));
```

Now message "Hello" will be delivered to receiver every 5 seconds.

### Replacing an existing job ###

Pass `ScheduleJobOptions.Replacing` to replace a job that already exists instead of sending `RemoveJob` and then a create command:

```csharp
var jobKey = new JobKey("greeting-job");
var triggerKey = new TriggerKey("greeting-trigger");

ITrigger Trigger(string cron) => TriggerBuilder.Create()
    .WithIdentity(triggerKey)
    .ForJob(jobKey)
    .WithCronSchedule(cron)
    .Build();

quartzActor.Tell(new CreateJob(receiver, "Hello", Trigger("0 0 0 1 1 ?")));
quartzActor.Tell(new CreateJob(receiver, "Hello again", Trigger("0/5 * * * * ?"), ScheduleJobOptions.Replacing));
```

Replacement matches on keys, so name both the job and the trigger:

- **Reuse the same job and trigger keys** to replace a job. The stored job keeps its identity and takes the new message and schedule.
- **A different trigger key adds a second schedule** rather than replacing the first. The job then fires on both triggers, because replacing a trigger does not delete the other triggers that point at the same job.
- **Without `Replacing`**, scheduling a key that already exists fails with `ObjectAlreadyExistsException` and the actor replies with `CreateJobFail`.

If the trigger names no job with `ForJob(...)`, the actor derives the job key from the trigger's name and group, so repeated commands for the same trigger identity address the same job.

## PersistentActor ##
 The persistent quartz scheduling actor. This allows the jobs to be persisted in the Quartz jobstore and then to work in a new instance of application with new incarnations of the actors.

Pass the Quartz properties for a persistent job store (or supply an existing `IScheduler`); without them Quartz uses an in-memory store and nothing survives a restart:

```csharp
var properties = new NameValueCollection
{
    [QuartzActor.PropertySchedulerInstanceName] = "QuartzScheduler",
    ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.LocalTransactionJobStore, Quartz",
    ["quartz.jobStore.dataSource"] = "default",
    ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.SQLiteDelegate, Quartz",
    ["quartz.dataSource.default.provider"] = "SQLite-Microsoft",
    ["quartz.dataSource.default.connectionString"] = "Data Source=quartz-jobs.db",
    ["quartz.serializer.type"] = "newtonsoft"
};
var quartzPersistentActor = Sys.ActorOf(Props.Create(() => new QuartzPersistentActor(properties)), "QuartzActor");
quartzPersistentActor.Tell(new CreatePersistentJob(receiver, "Hello", TriggerBuilder.Create().WithCronSchedule("*0/10 * * * * ?").Build()));
```

For more information, please see the unit test.

For more information about quartz scheduler please see
http://www.quartz-scheduler.net/documentation/

For more information about akka.net please see
https://getakka.net/articles/intro/what-is-akka.html
