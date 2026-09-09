## Summary

The Quartz.NET 4.0 / .NET 10 upgrade is complete. Summary of what was done:

### Framework & versions

- `Directory.Build.props`: collapsed the three TFM properties into a single `NetFrameworkTestVersion = net10.0`
- All three `.csproj` files now target `net10.0` only (dropped the OS-conditional multi-targeting)
- `Directory.Packages.props`: bumped every package to its latest stable version (`Quartz` 4.0.1, `Akka` 1.5.71, etc.), swapped `Quartz.Serialization.Json` → `Quartz.Serialization.Newtonsoft`
- `global.json`: SDK pinned to `10.0.400`
- `Directory.Generated.props` / `RELEASE_NOTES.md`: version bumped to `1.6.0-beta1` with changelog entries

### Source-level fixes required by Quartz 4.0's breaking API changes

(beyond what the original scoping doc anticipated)

- `QuartzActor.cs`: replaced the removed `StdSchedulerFactory` with `QuartzSchedulerBuilder`/`StandaloneSchedulerFactory`, disposed properly in `PostStop`
- `QuartzJob.cs` / `QuartzPersistentJob.cs`: `IJob.Execute` now returns `ValueTask` and takes a `CancellationToken`; `JobBuilder` is now static, so `CreateBuilderWithData` returns `JobBuilder<T>`
- `QuartzPersistentActor.cs`: fixed a **real pre-existing bug** — `Scheduler.ScheduleJob(...)` was never awaited (fire-and-forget), which Quartz 3.x's `Task` API silently tolerated but Quartz 4.0's `ValueTask` API does not — the write was being abandoned mid-flight. Now properly awaited via `ActorTaskScheduler.RunTask`
- `QuartzPersistentJob.cs`: Quartz 4.0's JSON serializer refuses raw `byte[]` in `JobDataMap`; the Akka-serialized message payload is now Base64-encoded to a string
- Integration test: updated to Quartz 4.0's renamed ADO store types, the new built-in `SQLite-Microsoft` provider (replacing the removed `DbProvider.RegisterDbMetadata`), and refreshed `tables_sqlite.sql` to the current Quartz 4.0 schema (added `qrtz_PAUSED_JOB_GRPS` and new trigger columns)
- One unit test (`QuartzPersistentActor_Should_Handle_New_Incarnations`) was rewritten per project direction, since it relied on Quartz 3.x's process-wide scheduler registry, which 4.0 intentionally removed

All 11 unit tests, the SQLite-backed integration test, and `dotnet pack` (producing `Akka.Quartz.Actor.1.6.0-beta1.nupkg`) pass.

### xunit v2 → xunit.v3

- `Directory.Packages.props`: `xunit` (v2) → `xunit.v3` 4.0.0; `Akka.TestKit.Xunit2` → `Akka.TestKit.Xunit` (Akka.NET's xunit.v3-based TestKit, at `$(AkkaVersion)`) — there is no `Akka.TestKit.Xunit3` package; Akka.NET reused the plain `Akka.TestKit.Xunit` package id for its v3 support
- Both test `.csproj` files: `<OutputType>Exe</OutputType>` and `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` (xunit.v3 test projects are self-executing rather than plain libraries)
- Source: `TestKit.Xunit2.TestKit` → `TestKit.Xunit.TestKit` base class in all three spec files; dropped `using Xunit.Abstractions;` in the integration test (`ITestOutputHelper` moved to the plain `Xunit` namespace in v3)
- `global.json`: added `"test": { "runner": "Microsoft.Testing.Platform" }` — required on .NET 10 SDK for `dotnet test` to use the new native Microsoft.Testing.Platform runner instead of the now-removed VSTest bridge
- Note: with native MTP, `dotnet test` must be invoked as `dotnet test --project <path-to-csproj>` (or run the built `.exe` directly) — running `dotnet test <directory>` silently reports "Zero tests ran"

All 12 tests (11 unit + 1 integration) still pass under the new xunit.v3/MTP setup. 30 new `xUnit1051` advisory warnings appeared (xunit v3's analyzer suggesting `TestContext.Current.CancellationToken` be threaded through TestKit calls) — left as-is, not fixed, since they're advisory only.
