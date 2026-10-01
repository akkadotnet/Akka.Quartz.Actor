# Upgrading Akka.Quartz.Actor to Quartz 4

Akka.Quartz.Actor **1.5.71-beta1** stays aligned with **Akka.NET 1.5.71**, and requires **.NET 10** and **Quartz 4.0.1**. This is a breaking platform and dependency upgrade despite the package remaining in the 1.5 series. Applications on .NET Framework, .NET Standard, .NET 8 or .NET 9 must remain on **1.5.59** until they can move to .NET 10. Pin the old package explicitly if you cannot migrate yet.

RAM-only users need the runtime, package and code changes below, but have no database to migrate. Existing ADO stores need a planned, offline cutover. Installing this package does **not** convert binary storage, repair cron expressions or upgrade the database schema.

## Rehearse on a backup first

Record the old runtime, package versions, connection settings, scheduler instance name, table prefix, serializer configuration, paused groups, actor paths and Akka message serializer bindings. Keep them with a consistent database backup. Preserve the scheduler name and table prefix during migration; changing either can make saved jobs appear missing.

Test with the actual application message types, calendars, serializer registrations and deployment time zones. Decide how overdue jobs should behave after downtime: catch up, fire once, or skip. Preserve each trigger's intended misfire instruction, and verify it on a restored backup. The helper never changes fire times or chooses a misfire policy.

## Standalone helpers: SQL Server, PostgreSQL and SQLite

The release archive contains two separate command-line applications. They never start Quartz, fire jobs or contact actors. The converter runs under .NET 8 with Quartz 3.14; the auditor runs under .NET 10 with Quartz 4.0.1. Install both runtimes on the migration machine. You can build the same archive contents from a source checkout with the SDK in `global.json`:

```powershell
pwsh -File scripts/publishUpgradeTools.ps1
```

The following commands assume you are in the extracted archive or `artifacts/upgrade-tools`. Both CLIs support **SQL Server, PostgreSQL and SQLite**. SQLite uses `--database`; SQL Server and PostgreSQL use `--provider sqlserver|postgres --connection-string-env <variable-name>`. Connection strings are read from the named environment variable and never printed. The examples below use SQLite; select the connection options for your provider and use the same dry-run, apply and audit steps:

| Provider | Connection options | Default-schema example prefix |
| --- | --- | --- |
| SQLite | `--provider sqlite --database backup.db` | `QRTZ_` |
| SQL Server | `--provider sqlserver --connection-string-env QUARTZ_CONNECTION` | `dbo.QRTZ_` |
| PostgreSQL | `--provider postgres --connection-string-env QUARTZ_CONNECTION` | `public.QRTZ_` |

For example, supply your connection string through the shell or secret manager, then run:

```shell
dotnet quartz3-convert/Quartz3Migration.dll --provider sqlserver --connection-string-env QUARTZ_CONNECTION --prefix dbo.QRTZ_ --scheduler QuartzScheduler --trusted-backup
dotnet quartz4-audit/Akka.Quartz.Actor.Upgrade.dll --provider postgres --connection-string-env QUARTZ_CONNECTION --prefix public.QRTZ_ --scheduler QuartzScheduler
```

The environment variable's connection string must select the existing target database. The helpers create no databases or tables. Prefixes may include one unquoted schema name; PostgreSQL resolves unquoted identifiers to lowercase, matching the official Quartz scripts. Custom quoted/case-sensitive identifiers need application-specific adaptation. For audit/dry-run, SQLite opens read-only and PostgreSQL sessions enforce read-only transactions. SQL Server auditing executes only queries; use a login with read-only permissions for database-enforced protection. Stop all writers during a production conversion regardless of provider.

### 1. Convert binary storage while Quartz 3 is still available

Skip this conversion if your store already uses compatible Newtonsoft JSON. A message's legacy `byte[]` value *inside JSON* is supported by the new actor and does not need conversion. A BinaryFormatter-encoded database blob is different: Quartz 4 cannot read it.

Use an offline backup produced from a trusted application. BinaryFormatter deserialization can execute code from the saved types, including during a dry run. The helper requires an explicit acknowledgement and must not be used on an untrusted database.

```shell
dotnet quartz3-convert/Quartz3Migration.dll --database backup.db --trusted-backup --scheduler QuartzScheduler
```

This validates and reports binary, JSON and empty blob counts without writing. Inspect failures before proceeding. Load required .NET 8-compatible application types with repeatable `--assembly /path/to/application.dll`; sibling dependencies are resolved from that directory. The helper converts plain `Dictionary<string, object>` and `Dictionary<object, object>` maps with string keys and default key comparison (ordinal comparison is also supported for string dictionaries). Values may contain only null, strings, booleans, integer values and byte arrays. It verifies every recovered value against its original, including Akka message bytes, before writing. JSON can change integer boxing (for example, `int` to `long`); rehearse jobs that rely on exact boxed types. Strings parsed as dates, floating-point values, nested objects, custom map types/comparers and arbitrary application objects are rejected rather than risking silent changes. Unsupported values need application-specific conversion while Quartz 3 is available; merely loading their assembly does not make them safe to convert.

The converter supports Quartz's stock Base, Annual, Cron, Daily, Holiday, Monthly and Weekly calendars, including stock base chains, only when their scheduling settings survive the JSON round trip unchanged. It checks calendar types, exclusions/ranges/cron expressions, descriptions, time zones and their rules, and base chains. It also checks DailyCalendar's next-included-time precision, which can differ between Quartz 3's binary and JSON readers. Any mismatch requires application-specific migration. Custom calendar subclasses are rejected. The helper preserves identities, fire times and other schedule columns.

Custom serializers, unsupported calendar types and **nonempty `QRTZ_BLOB_TRIGGERS`** need application-specific migration. The helper refuses them and rolls back the transaction instead of guessing a format. Loading an assembly does not install its serializer registrations.

Once the rehearsal succeeds, stop **every** scheduler using the production store, including other Quartz applications sharing its tables, and take the final consistent backup. Run against the intended cutover database:

```shell
dotnet quartz3-convert/Quartz3Migration.dll --database production.db --trusted-backup --scheduler QuartzScheduler --apply --schedulers-stopped
```

`--apply` is required to write, and requires `--schedulers-stopped`. This acknowledgement cannot detect running nodes for you. Conversion commits once after validating all selected rows; an error rolls back earlier updates. Re-running it leaves already converted JSON unchanged. Keep the backup until the entire application cutover is verified. Do not restart old nodes with their binary serializer against the converted store. If you must run Quartz 3 again before cutover, use its Newtonsoft serializer and verify it on the rehearsal copy first.

Both helpers accept `--prefix QRTZ_`. `--scheduler` limits the selected rows and fails if no stored rows match that name, so a typo cannot produce a successful empty audit or conversion. Omit it only when deliberately checking/converting every scheduler in those tables.

### 2. Audit with the exact Quartz 4 parser before schema migration

```shell
dotnet quartz4-audit/Akka.Quartz.Actor.Upgrade.dll --database production.db --scheduler QuartzScheduler
```

The auditor opens the existing file read-only. It checks saved cron expressions using **Quartz 4.0.1**, host time zone availability, and Newtonsoft readability of serialized data and calendars. Job and trigger data are read as `JobDataMap`, matching the Quartz 4 ADO reader; a successful generic dictionary read is insufficient. It flags remaining binary blobs and custom BLOB triggers. Missing tables or unreadable schema are failures, not a successful zero-row audit. Required application assemblies can be supplied with `--assembly`.

Both helpers exit **0** after a successful operation and **1** on an error; the auditor also exits 1 if it finds issues. Reports identify affected rows without printing message payloads. `--help` displays usage. A clean audit checks the formats above; it does not prove actor delivery, validate every job class or install custom serializer registrations.

Quartz **4.0.1 rejects** textual day-of-week steps such as `MON/2`. Choose the intended replacement while you can still inspect the original schedule; the helper reports the expression but never rewrites it. Also audit cron expressions in application configuration, code and custom calendars. The database audit cannot find schedules that have not been saved. Do not use later Quartz documentation's cron behavior as proof of 4.0.1 behavior.

### 3. Apply the official database upgrade script

With old nodes stopped, apply the matching **`schema_30_to_40_upgrade_<db>.sql`** from the [Quartz 4.0.1 migration scripts](https://github.com/quartznet/quartznet/tree/v4.0.1/database/migrations/4.0). This adds required columns and `QRTZ_PAUSED_JOB_GRPS`. Adapt the prefix if your installation uses one. The helpers do not execute DDL; use your normal database deployment tooling and retain its execution record.

Never apply `tables_<db>.sql` to an existing store: these fresh-install scripts drop tables and erase jobs. The optional `schema_30_to_40_indexes_<db>.sql` must only run after the final Quartz 3 node is stopped. Reapply intended paused job groups using Quartz 4's pause API and verify their state; creating the new table does not recreate those settings automatically.

## Update the application

Target .NET 10 and reference Akka.Quartz.Actor 1.5.71-beta1 and the aligned Akka.NET 1.5.71 packages. Upgrade all direct Quartz dependencies together to 4.0.1. Replace `Quartz.Serialization.Json` with `Quartz.Serialization.Newtonsoft`; remove obsolete 3.x `Quartz.Extensions.DependencyInjection`, `Quartz.Extensions.Hosting` and `Quartz.Serialization.SystemTextJson` references, whose APIs moved into Quartz itself.

For an existing Newtonsoft store, explicitly configure:

```text
quartz.serializer.type = newtonsoft
quartz.jobStore.type = Quartz.Impl.AdoJobStore.LocalTransactionJobStore, Quartz
```

Reference `Quartz.Serialization.Newtonsoft` in the application. Quartz 4's `json` alias selects System.Text.Json, whereas older configurations may have used it for Newtonsoft. Do not switch serializer families as part of this cutover without a separate tested migration. `JobStoreTX` becomes `LocalTransactionJobStore`; `JobStoreCMT` becomes `AmbientTransactionJobStore`. See the pinned upstream guide for custom store/delegate changes.

Applications that implement or call Quartz APIs must rebuild. `IJob.Execute` returns `ValueTask` and receives a `CancellationToken`; `Quartz.Spi` becomes `Quartz.Extensibility`, `Quartz.Simpl` becomes `Quartz.Impl`, and `StdSchedulerFactory` is replaced by `QuartzSchedulerBuilder`. Scheduler existence, lifecycle and scheduling APIs also changed. Follow compiler errors and the upstream guide rather than replacing only the actor package.

Quartz 4 has no process-wide scheduler registry: constructing another actor with the same instance name does not share the first actor's RAM scheduler. Share an explicitly supplied scheduler if you require shared ownership. An actor disposes schedulers it creates; it leaves a supplied scheduler to its caller.

### Install the actor context before starting a supplied scheduler

Actor-owned schedulers now install the persistent actor context before starting. For a supplied scheduler, create receivers and the persistent actor first, then wait for its mailbox to be ready before starting Quartz. Do not allow a hosted service to start that scheduler earlier.

```csharp
await using var factory = QuartzSchedulerBuilder.Create().UseProperties(properties).Build();
var scheduler = await factory.GetScheduler(cancellationToken);
var receiver = system.ActorOf(Props.Create(() => new Receiver()), "receiver");
var quartzActor = system.ActorOf(
    Props.Create(() => new QuartzPersistentActor(scheduler)), "quartz");
await quartzActor.Ask<ActorIdentity>(new Identify(null), TimeSpan.FromSeconds(10), cancellationToken);
await scheduler.Start(cancellationToken);
```

This snippet assumes `Akka.Actor` and `Quartz` imports and your existing receiver/configuration. Keep actor paths and Akka serializer IDs/manifests compatible with saved messages. Existing `OnSchedulerCreated` overrides still run after an actor-owned scheduler starts; persistent actor context is installed separately beforehand. Supplied schedulers retain their caller-controlled startup state when the callback runs. When sharing a scheduler, use a single intended ActorSystem context for its persistent jobs.

### Await scheduler shutdown before touching the store

Stopping an actor initiates owned scheduler shutdown, but actor `Terminated` alone is not proof that plugins, jobs or the job store have stopped. With the default Akka coordinated shutdown enabled, `await system.Terminate()` also awaits actor-owned scheduler shutdown in `before-actor-system-terminate`, including running jobs. Configure that phase's timeout for your longest expected job/plugin shutdown, and investigate any timeout or failure before migrating. Disabling coordinated shutdown, recovering from its failures, or letting its phase timeout expire removes that assurance.

For a strict migration boundary, use a caller-owned scheduler and explicitly await its shutdown while the ActorSystem and receivers are still available:

```csharp
await scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken);
await factory.DisposeAsync();
await system.Terminate();
```

Do not proceed if shutdown fails or is cancelled. Repeat the shutdown procedure for every node and every other application sharing the store before taking the final backup and applying conversion/DDL. Factory disposal alone does not request a graceful drain of running jobs.

## Cutover and rollback

Rehearse restoring the backup, conversion if needed, auditing, DDL, application startup and **natural scheduled delivery**. Check custom message deserialization after a full process restart, overdue jobs, paused groups and the next fire times. Then perform the same sequence offline in production. Do not mix old and new Akka.Quartz.Actor writers: new jobs use Base64 message strings that the old package cannot read, and Quartz 4 schema/serializer changes add further compatibility boundaries.

Once new jobs or calendars have been written, application-only downgrade is unsafe. Stop the new deployment and restore the matching pre-cutover database, application and configuration together. Reconcile jobs created or fired since the backup, including external side effects and possible duplicate deliveries, before resuming. Neither helper reverses the upgrade or guarantees exactly-once delivery.

## What is verified here

Local tests exercise actual SQL Server 2022 and PostgreSQL 17 containers using pinned upstream Quartz 3 schemas and Quartz 4 upgrade scripts, then verify natural actor delivery. They cover conversion dry-run/apply, rollback after a bad calendar, unchanged schedule times, idempotence and invalid cron detection on both engines. SQLite tests cover migration from real Quartz 3 persisted data, binary helper dry-run/apply acknowledgements, transactional rollback, scheduler scoping, unchanged schedule columns and message bytes, idempotence, actual Quartz 3 JSON reads, Quartz 4 calendar/trigger reads and natural actor delivery after conversion. They also cover a typed application message after a fresh scheduler/ActorSystem restart, an overdue one-shot's explicit FireNow policy, failed database writes, startup ordering and scheduler ownership.

Database engines other than SQL Server, PostgreSQL and SQLite, custom serializers/calendars/BLOB triggers, distributed clustered cutovers and every misfire policy remain application-specific validation work. CI must discover all test projects, fail on each native exit code and require fresh reports with executed tests. The database test project requires Docker running Linux containers. Linux CI runs it; Windows hosted CI runs the remaining projects. Tagged releases also run the complete Linux validation job on that tag's revision, and publishing depends on its success. Run the complete checks locally on a Docker-capable host with:

```powershell
dotnet build -c Release
pwsh -File scripts/tests/runTests.Tests.ps1
pwsh -File scripts/runTests.ps1
```
