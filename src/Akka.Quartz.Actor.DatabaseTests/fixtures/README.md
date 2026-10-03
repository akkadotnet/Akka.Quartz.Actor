These are upstream Quartz SQL fixtures with trailing whitespace normalized:

| Local file | Upstream source |
| --- | --- |
| `quartz3_sqlserver.sql` | [Quartz 3.14.0 SQL Server schema](https://github.com/quartznet/quartznet/blob/v3.14.0/database/tables/tables_sqlServer.sql) |
| `quartz3_postgres.sql` | [Quartz 3.14.0 PostgreSQL schema](https://github.com/quartznet/quartznet/blob/v3.14.0/database/tables/tables_postgres.sql) |
| `upgrade_sqlserver.sql` | [Quartz 4.0.1 SQL Server upgrade](https://github.com/quartznet/quartznet/blob/v4.0.1/database/migrations/4.0/schema_30_to_40_upgrade_sqlServer.sql) |
| `upgrade_postgres.sql` | [Quartz 4.0.1 PostgreSQL upgrade](https://github.com/quartznet/quartznet/blob/v4.0.1/database/migrations/4.0/schema_30_to_40_upgrade_postgres.sql) |

Quartz.NET is licensed under Apache 2.0. The schema scripts are destructive and are used only in newly created disposable test databases. They are never invoked by the migration helpers. The test replaces SQL Server's database-name placeholder in memory.

The linked `quartz3_binary_blobs.sql` was generated using Quartz 3.14.0's `BinaryObjectSerializer`: the real legacy fixture's job dictionary (including its existing Akka message bytes), a trigger dictionary with `count=123`, and a `HolidayCalendar`. Tests convert it with the .NET 8 helper, then use the separate Quartz 4 reader and naturally scheduled actor execution to verify compatibility.
