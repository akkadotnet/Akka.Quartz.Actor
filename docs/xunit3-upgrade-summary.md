## Summary

The upgrade to xunit.v3 is complete. Summary:

**Packages** — `xunit` (v2) → `xunit.v3` 4.0.0; `Akka.TestKit.Xunit2` → `Akka.TestKit.Xunit` (there's no `Akka.TestKit.Xunit3` — Akka.NET reused the plain `Akka.TestKit.Xunit` package id for its xunit.v3-based TestKit).

**Project files** — both test `.csproj`s now build as executables (`OutputType=Exe`) with `UseMicrosoftTestingPlatformRunner=true`, since xunit.v3 test projects are self-executing rather than library-hosted.

**Source** — `TestKit.Xunit2.TestKit` → `TestKit.Xunit.TestKit` in all three spec files; dropped `using Xunit.Abstractions;` (`ITestOutputHelper` moved to the plain `Xunit` namespace in v3).

**`global.json`** — added `"test": {"runner": "Microsoft.Testing.Platform"}`, required on .NET 10 SDK since VSTest-based `dotnet test` is no longer supported there.

One gotcha worth flagging: with native MTP, `dotnet test <directory>` silently reports "Zero tests ran" — you now need `dotnet test --project <path-to-csproj>` (or run the built `.exe` directly, or `dotnet test` at the solution level, which also works).

All 12 tests (11 unit + 1 integration) pass. Build produces 30 new `xUnit1051` advisory warnings (suggesting `TestContext.Current.CancellationToken` be threaded through TestKit calls) — left alone since they're non-blocking style suggestions, not correctness issues. `RELEASE_NOTES.md` and `docs/quartz4-upgrade-summary.md` are updated with these details.
