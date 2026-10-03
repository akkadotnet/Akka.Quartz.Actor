#### 1.5.71-beta1 September 30 2026 ####

* Align with **Akka.NET 1.5.71**; upgrade to **Quartz 4.0.1** and target **.NET 10 only**. This drops .NET Standard/.NET Framework and older .NET support despite retaining aligned 1.5 package numbering. Users who cannot move to .NET 10 should pin Akka.Quartz.Actor **1.5.59**.
* **Read the [upgrade guide](https://github.com/akkadotnet/Akka.Quartz.Actor/blob/dev/docs/upgrading-to-quartz4.md) before upgrading.** It is also included in the NuGet package (`docs/upgrading-to-quartz4.md`) and the separate upgrade-helper archive. It walks through each upgrade path step by step, with screenshots from a real upgrade of a store written by Akka.Quartz.Actor 1.5.59; `examples/upgrade-walkthrough` reproduces it, and CI runs it for binary and JSON stores.
* Existing ADO stores require the [official Quartz 4.0.1 schema upgrade script](https://github.com/quartznet/quartznet/tree/v4.0.1/database/migrations/4.0), a cron audit and an offline cutover. Never use the destructive fresh-install `tables_<db>.sql` scripts. Quartz **4.0.1 rejects** textual weekday steps such as `MON/2`; select replacements deliberately.
* Added separate Quartz 3 binary-to-Newtonsoft converter and Quartz 4 read-only auditor, both running on .NET 10, for **SQL Server, PostgreSQL and SQLite**. Conversion defaults to dry-run, requires explicit trust and stopped-scheduler acknowledgements to apply, preserves schedule columns, and rolls back on failure. Custom BLOB triggers require application-specific migration. Neither helper starts a scheduler or applies schema changes.
* Legacy Akka message `byte[]` values inside compatible Newtonsoft JSON remain readable. This does **not** make BinaryFormatter-backed database blobs readable: convert those before leaving Quartz 3. Newly saved messages use Base64 strings, so mixed old/new writers and application-only rollback are unsafe; see the upgrade guide.
* Replace `Quartz.Serialization.Json` with `Quartz.Serialization.Newtonsoft`, explicitly keep `quartz.serializer.type=newtonsoft` for existing Newtonsoft stores, and update direct Quartz packages/APIs together. `IJob.Execute` returns `ValueTask` and receives a `CancellationToken`; see the [pinned upstream migration guide](https://github.com/quartznet/quartznet/blob/v4.0.1/docs/documentation/quartz-4.x/migration-guide.md).
* Quartz 4 removed the process-wide scheduler registry. Identical scheduler names no longer share a scheduler, so `QuartzPersistentActor(string)` always creates a new **in-memory** scheduler and is now `[Obsolete]`. Use the new `QuartzPersistentActor(NameValueCollection)` constructor with your job store properties, or supply a scheduler with `QuartzPersistentActor(IScheduler)`.
* A restarted actor no longer creates its scheduler until the previous incarnation's scheduler with the same instance name has finished shutting down, so two schedulers never share a job store. A scheduler whose shutdown fails is still released, so the failure cannot block the restart or fail a later coordinated shutdown.
* Persistent jobs whose stored message is neither `byte[]` nor valid Base64 now fail with a `JobExecutionException` that names the job, instead of silently doing nothing.
* Install persistent actor context **before** starting an actor-owned scheduler while preserving the existing post-start `OnSchedulerCreated` callback. Documented readiness ordering for caller-owned schedulers; coordinated shutdown now awaits actor-owned schedulers and running jobs. See the guide for timeout limits and an explicit shutdown boundary before migration.
* Hardened migration validation: reject unsupported binary object state and changed values, and audit job/trigger data using Quartz 4's actual `JobDataMap` reader. Tagged releases must pass full Linux validation, including database migrations, before publishing.
* Invalid job requests return one failure and create no job. Added real SQL Server/PostgreSQL container conversion and delivery checks, plus SQLite binary-conversion, rollback, restart, misfire, failed-write, schema and audit regressions and scheduler lifecycle checks.
* Run all `*Tests.csproj` projects in CI, fail immediately on each test process failure and require fresh reports with executed tests. Test projects use xunit.v3 / Microsoft.Testing.Platform.

#### 1.5.59 January 26 2026 ####

* [Update Akka.NET to v1.5.59](https://github.com/akkadotnet/akka.net/releases/tag/1.5.59)

#### 1.5.33 January 3 2025 ####

* [Update Akka.NET to v1.5.33](https://github.com/akkadotnet/akka.net/releases/tag/1.5.33)
* Updated .NET test version to 8.0
* Changed quartz.serializer.type in integration tests from "binary" to "newtonsoft" (binary formatter is deprecated in Quartz.NET and .NET)

#### 1.5.13 October 4 2023 ####

* [Update Akka.NET to v1.5.13](https://github.com/akkadotnet/akka.net/releases/tag/1.5.13)

#### 1.5.12 September 5 2023 ####

* [Update Akka.NET to v1.5.12](https://github.com/akkadotnet/akka.net/releases/tag/1.5.12)
* [Fix persisted job serialization to always serialize using object serializer](https://github.com/akkadotnet/Akka.Quartz.Actor/pull/312)
* Upgraded all other dependencies

#### 1.5.1 March 27 2022 ####

* [Update Akka.NET to v1.5.1](https://github.com/akkadotnet/akka.net/releases/tag/1.5.1)
* Upgraded all other dependencies
