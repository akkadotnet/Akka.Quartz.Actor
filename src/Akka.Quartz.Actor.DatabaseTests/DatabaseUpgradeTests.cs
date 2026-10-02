using System.Collections.Specialized;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Akka.Actor;
using Microsoft.Data.SqlClient;
using Npgsql;
using Quartz;
using Quartz.Impl.Calendar;
using QuartzUpgradeTests;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Akka.Quartz.Actor.DatabaseTests;

public sealed class DatabaseUpgradeTests
{
    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task Helpers_Should_Migrate_A_Real_Store_And_Quartz4_Should_Deliver(string provider)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (provider == "sqlserver")
        {
            await using var server = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU18-ubuntu-22.04").Build();
            await server.StartAsync(cancellationToken);
            await using (var admin = new SqlConnection(server.GetConnectionString()))
            {
                await admin.OpenAsync(cancellationToken);
                await Execute(admin, "CREATE DATABASE quartz_upgrade");
            }
            var settings = new SqlConnectionStringBuilder(server.GetConnectionString()) { InitialCatalog = "quartz_upgrade" };
            await using var connection = new SqlConnection(settings.ToString());
            await connection.OpenAsync(cancellationToken);
            await Verify(provider, connection, "dbo.QRTZ_", settings.ToString());
        }
        else
        {
            await using var server = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("quartz_upgrade").Build();
            await server.StartAsync(cancellationToken);
            await using var connection = new NpgsqlConnection(server.GetConnectionString());
            await connection.OpenAsync(cancellationToken);
            await Verify(provider, connection, "public.QRTZ_", server.GetConnectionString());
        }
    }

    private static async Task Verify(string provider, DbConnection connection, string prefix, string connectionString)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", $"quartz3_{provider}.sql"), cancellationToken);
        // Fresh-install scripts are used ONLY inside this test's brand-new, disposable database.
        schema = schema.Replace("USE [enter_db_name_here];", "USE [quartz_upgrade];");
        await ExecuteScript(provider, connection, schema);
        var binaryFixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "quartz3_binary_blobs.sql"), cancellationToken);
        var blobs = Regex.Matches(binaryFixture, "x'([0-9A-F]+)'").Select(match => System.Convert.FromHexString(match.Groups[1].Value)).ToArray();
        Assert.Equal(3, blobs.Length);
        var fireTime = DateTimeOffset.UtcNow.AddDays(1).UtcDateTime.Ticks;
        await Execute(connection, "INSERT INTO QRTZ_JOB_DETAILS (SCHED_NAME,JOB_NAME,JOB_GROUP,JOB_CLASS_NAME,IS_DURABLE,IS_NONCONCURRENT,IS_UPDATE_DATA,REQUESTS_RECOVERY,JOB_DATA) VALUES ('QuartzScheduler','legacy-job','DEFAULT','Akka.Quartz.Actor.QuartzPersistentJob, Akka.Quartz.Actor',@yes,@no,@no,@no,@blob)",
            ("@yes", true), ("@no", false), ("@blob", blobs[0]));
        await Execute(connection, "INSERT INTO QRTZ_TRIGGERS (SCHED_NAME,TRIGGER_NAME,TRIGGER_GROUP,JOB_NAME,JOB_GROUP,NEXT_FIRE_TIME,PRIORITY,TRIGGER_STATE,TRIGGER_TYPE,START_TIME,MISFIRE_INSTR,JOB_DATA) VALUES ('QuartzScheduler','legacy-trigger','DEFAULT','legacy-job','DEFAULT',@time,5,'WAITING','SIMPLE',@time,0,@blob)",
            ("@time", fireTime), ("@blob", blobs[1]));
        await Execute(connection, "INSERT INTO QRTZ_SIMPLE_TRIGGERS VALUES ('QuartzScheduler','legacy-trigger','DEFAULT',-1,3600000,0)");
        await Execute(connection, "INSERT INTO QRTZ_CALENDARS VALUES ('QuartzScheduler','holiday',@blob)", ("@blob", blobs[2]));
        var environment = new Dictionary<string, string> { ["QUARTZ_TEST_CONNECTION"] = connectionString };
        var options = new[] { "--provider", provider, "--connection-string-env", "QUARTZ_TEST_CONNECTION", "--prefix", prefix, "--scheduler", "QuartzScheduler" };
        async Task<(int ExitCode, string Output)> Convert(params string[] flags) =>
            await HelperProcess.Run("Quartz3Migration", options.Concat(new[] { "--trusted-backup" }).Concat(flags).ToArray(), environment);
        async Task<(int ExitCode, string Output)> Audit() => await HelperProcess.Run("Akka.Quartz.Actor.Upgrade", options, environment);

        var preview = await Convert();
        Assert.True(preview.ExitCode == 0, preview.Output);
        Assert.Equal(3, JsonDocument.Parse(preview.Output).RootElement.GetProperty("BinaryBlobs").GetInt32());
        Assert.Equal(blobs[0], (byte[])(await Scalar(connection, "SELECT JOB_DATA FROM QRTZ_JOB_DETAILS"))!);
        Assert.Equal(1, (await Audit()).ExitCode);
        Assert.Equal(1, (await Convert("--apply")).ExitCode);
        Assert.Equal(blobs[0], (byte[])(await Scalar(connection, "SELECT JOB_DATA FROM QRTZ_JOB_DETAILS"))!);

        // A later bad calendar must roll back job/trigger blobs that have already been updated.
        await Execute(connection, "UPDATE QRTZ_CALENDARS SET CALENDAR=@blob", ("@blob", new byte[] { 0, 1, 0, 0 }));
        var failed = await Convert("--apply", "--schedulers-stopped");
        Assert.Equal(1, failed.ExitCode);
        Assert.Equal(blobs[0], (byte[])(await Scalar(connection, "SELECT JOB_DATA FROM QRTZ_JOB_DETAILS"))!);
        Assert.Equal(blobs[1], (byte[])(await Scalar(connection, "SELECT JOB_DATA FROM QRTZ_TRIGGERS"))!);
        await Execute(connection, "UPDATE QRTZ_CALENDARS SET CALENDAR=@blob", ("@blob", blobs[2]));
        var applied = await Convert("--apply", "--schedulers-stopped");
        Assert.True(applied.ExitCode == 0, applied.Output);
        Assert.Equal(fireTime, System.Convert.ToInt64(await Scalar(connection, "SELECT NEXT_FIRE_TIME FROM QRTZ_TRIGGERS")));
        Assert.Equal(fireTime, System.Convert.ToInt64(await Scalar(connection, "SELECT START_TIME FROM QRTZ_TRIGGERS")));
        var repeated = await Convert("--apply", "--schedulers-stopped");
        Assert.True(repeated.ExitCode == 0, repeated.Output);
        Assert.Equal(0, JsonDocument.Parse(repeated.Output).RootElement.GetProperty("BinaryBlobs").GetInt32());
        Assert.Equal(0, (await Audit()).ExitCode);

        await Execute(connection, "INSERT INTO QRTZ_TRIGGERS SELECT SCHED_NAME,'bad',TRIGGER_GROUP,JOB_NAME,JOB_GROUP,DESCRIPTION,NEXT_FIRE_TIME,PREV_FIRE_TIME,PRIORITY,TRIGGER_STATE,'CRON',START_TIME,END_TIME,CALENDAR_NAME,MISFIRE_INSTR,NULL FROM QRTZ_TRIGGERS WHERE TRIGGER_NAME='legacy-trigger'");
        await Execute(connection, "INSERT INTO QRTZ_CRON_TRIGGERS VALUES ('QuartzScheduler','bad','DEFAULT','0 0 9 ? * MON/2','Mars/Olympus_Mons')");
        var invalidCron = await Audit();
        Assert.Equal(1, invalidCron.ExitCode);
        Assert.Contains("MON/2", invalidCron.Output);
        // The rejected cron expression must not stop the audit from checking the same row's time zone.
        Assert.Contains("cannot resolve this time zone", invalidCron.Output);
        Assert.Equal("0 0 9 ? * MON/2", await Scalar(connection, "SELECT CRON_EXPRESSION FROM QRTZ_CRON_TRIGGERS"));
        await Execute(connection, "DELETE FROM QRTZ_CRON_TRIGGERS WHERE TRIGGER_NAME='bad'; DELETE FROM QRTZ_TRIGGERS WHERE TRIGGER_NAME='bad'");
        await ExecuteScript(provider, connection, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", $"upgrade_{provider}.sql"), cancellationToken));
        var nearFuture = DateTimeOffset.UtcNow.AddSeconds(1).UtcDateTime.Ticks;
        await Execute(connection, "UPDATE QRTZ_TRIGGERS SET NEXT_FIRE_TIME=@time,START_TIME=@time", ("@time", nearFuture));
        var properties = new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "QuartzScheduler",
            ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.LocalTransactionJobStore, Quartz",
            ["quartz.jobStore.useProperties"] = "false",
            ["quartz.jobStore.dataSource"] = "default",
            ["quartz.jobStore.tablePrefix"] = prefix,
            ["quartz.jobStore.driverDelegateType"] = provider == "sqlserver"
                ? "Quartz.Impl.AdoJobStore.SqlServerDelegate, Quartz" : "Quartz.Impl.AdoJobStore.PostgreSQLDelegate, Quartz",
            ["quartz.dataSource.default.provider"] = provider == "sqlserver" ? "SqlServer" : "Npgsql",
            ["quartz.dataSource.default.connectionString"] = connectionString,
            ["quartz.serializer.type"] = "newtonsoft"
        };
        await using var factory = QuartzSchedulerBuilder.Create().UseProperties(properties).Build();
        var scheduler = await factory.GetScheduler(cancellationToken);
        Assert.IsType<HolidayCalendar>(await scheduler.GetCalendar("holiday", cancellationToken));
        var savedJob = await scheduler.GetJobDetail(new JobKey("legacy-job"), cancellationToken);
        Assert.IsType<byte[]>(savedJob!.JobDataMap["message"]);
        var trigger = await scheduler.GetTrigger(new TriggerKey("legacy-trigger"), cancellationToken);
        Assert.Equal(123L, System.Convert.ToInt64(trigger!.JobDataMap["count"]));
        var delivery = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = ActorSystem.Create("compat-test");
        try
        {
            system.ActorOf(Props.Create(() => new Receiver(delivery)), "receiver");
            var actor = system.ActorOf(Props.Create(() => new QuartzPersistentActor(scheduler)), "quartz");
            await actor.Ask<ActorIdentity>(new Identify(null), TimeSpan.FromSeconds(5), cancellationToken);
            await scheduler.Start(cancellationToken);
            Assert.Equal("Hello from Quartz 3", await delivery.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken));
            Assert.True((await scheduler.GetTrigger(new TriggerKey("legacy-trigger"), cancellationToken))!.NextFireTimeUtc > DateTimeOffset.UtcNow);
        }
        finally { await system.Terminate(); }
    }

    private static async Task ExecuteScript(string provider, DbConnection connection, string sql)
    {
        var batches = provider == "sqlserver" ? Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase) : new[] { sql };
        foreach (var batch in batches)
            if (!string.IsNullOrWhiteSpace(batch)) await Execute(connection, batch);
    }

    private static async Task Execute(DbConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            if (value is byte[]) parameter.DbType = DbType.Binary;
            command.Parameters.Add(parameter);
        }
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<object?> Scalar(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private sealed class Receiver : ReceiveActor
    {
        public Receiver(TaskCompletionSource<string> delivery) => Receive<string>(message => delivery.TrySetResult(message));
    }
}
