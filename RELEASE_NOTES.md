#### 1.6.0-beta1 September 9 2026 ####

* Upgrade to [Quartz.NET 4.0](https://www.quartz-scheduler.net/posts/2026-09-03-quartznet-4.0-released.html)
* Drop support for .NET Standard and .NET Framework; target .NET 10 only
* Breaking: `IJob.Execute` now returns `ValueTask` and takes a `CancellationToken`, matching Quartz 4.0's `IJob` interface
* Replace `Quartz.Serialization.Json` with `Quartz.Serialization.Newtonsoft` (the former is now an empty shim package in Quartz 4.0)
* Behavioral change: Quartz 4.0 removed its process-wide scheduler registry, so two `QuartzPersistentActor`s given the same scheduler instance name no longer share one in-memory scheduler within a process. Only a persistent job store now lets a new incarnation recover jobs from an earlier one.
* Test projects upgraded from xunit v2 to xunit.v3, using the new `Akka.TestKit.Xunit` package; test projects now build as executables (`OutputType=Exe`) and run under Microsoft.Testing.Platform (`dotnet test --project <csproj>` or the built executable directly)
* Update all other dependencies to their latest stable versions

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
