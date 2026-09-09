## Introduction
The scope of work is to upgrade projects in this solution so they refence Quartz.NET 4.0 instead of 3.x.

Quartz 4.0 Release notes: https://www.quartz-scheduler.net/posts/2026-09-03-quartznet-4.0-released.html

## Requirements 
- Support for .NET Standard and .NET Framework is discontinued. All projects support only .NET 10.
- File Directory.Build.props contain PropertyGroup that defines NetFrameworkTestVersion, NetStandardLibVersion and NetTestVersion. They will all need to be replaced with a single entry NetFrameworkTestVersion that is set to net10.0.
- xunit should be upgraded to xunit.v3.
- Referenced NuGet packages should also be updated with latest compatible versions (no pre-release).
- The version of Akka.Quartz.Actor assemblies (including test projects) and NuGet package should be set to 1.6.0-beta1
- The solution should be built and tested after the upgrade.