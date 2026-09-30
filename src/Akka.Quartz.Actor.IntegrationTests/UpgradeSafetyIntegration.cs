using System;
using System.Collections.Specialized;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Quartz.Impl.Calendar;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Pattern;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Akka.Quartz.Actor.Upgrade;
using Microsoft.Data.Sqlite;
using Quartz;
using Xunit;

namespace Akka.Quartz.Actor.IntegrationTests
{
    public class UpgradeSafetyIntegration
    {
        [Fact]
        public async Task New_Application_Message_Should_Recover_After_Scheduler_And_ActorSystem_Restart()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var store = await Store.Create(migrate: true);
            var expected = new ScheduledEnvelope("invoice", 42);
            var properties = store.Properties("recovery");
            var firstSystem = ActorSystem.Create("recovery-test");
            try
            {
                await using var firstFactory = QuartzSchedulerBuilder.Create().UseProperties(properties).Build();
                var scheduler = await firstFactory.GetScheduler(cancellationToken);
                var receiver = firstSystem.ActorOf(Props.Create(() => new Receiver(new TaskCompletionSource<ScheduledEnvelope>())), "receiver");
                var actor = firstSystem.ActorOf(Props.Create(() => new QuartzPersistentActor(scheduler)), "quartz");
                var trigger = TriggerBuilder.Create().WithIdentity("new-trigger").ForJob("new-job")
                    .StartAt(DateTimeOffset.UtcNow.AddSeconds(1)).Build();
                await actor.Ask<JobCreated>(new CreatePersistentJob(receiver.Path, expected, trigger), TimeSpan.FromSeconds(5), cancellationToken);
                // The first scheduler never starts, so only the recreated application can deliver this job.
                Assert.IsType<string>((await scheduler.GetJobDetail(new JobKey("new-job"), cancellationToken)).JobDataMap["message"]);
            }
            finally { await firstSystem.Terminate(); }

            var secondSystem = ActorSystem.Create("recovery-test");
            try
            {
                await using var secondFactory = QuartzSchedulerBuilder.Create().UseProperties(properties).Build();
                var scheduler = await secondFactory.GetScheduler(cancellationToken);
                var delivery = new TaskCompletionSource<ScheduledEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
                secondSystem.ActorOf(Props.Create(() => new Receiver(delivery)), "receiver");
                var actor = secondSystem.ActorOf(Props.Create(() => new QuartzPersistentActor(scheduler)), "quartz");
                await actor.Ask<ActorIdentity>(new Identify(null), TimeSpan.FromSeconds(5), cancellationToken);
                await scheduler.Start(cancellationToken);
                Assert.Equal(expected, await delivery.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
            }
            finally { await secondSystem.Terminate(); }
        }

        [Fact]
        public async Task Overdue_Quartz3_OneShot_Should_Deliver_After_Migration_With_FireNow_Policy()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var store = await Store.Create(migrate: true);
            var past = DateTimeOffset.UtcNow.AddMinutes(-5).UtcDateTime.Ticks;
            await store.Execute($"UPDATE QRTZ_TRIGGERS SET START_TIME={past}, NEXT_FIRE_TIME={past}, MISFIRE_INSTR=1;"
                + "UPDATE QRTZ_SIMPLE_TRIGGERS SET REPEAT_COUNT=0, REPEAT_INTERVAL=0, TIMES_TRIGGERED=0;");
            await using var factory = QuartzSchedulerBuilder.Create().UseProperties(store.Properties()).Build();
            var scheduler = await factory.GetScheduler(cancellationToken);
            var delivery = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var system = ActorSystem.Create("compat-test");
            try
            {
                system.ActorOf(Props.Create(() => new LegacyReceiver(delivery)), "receiver");
                var actor = system.ActorOf(Props.Create(() => new QuartzPersistentActor(scheduler)), "quartz");
                await actor.Ask<ActorIdentity>(new Identify(null), TimeSpan.FromSeconds(5), cancellationToken);
                var trigger = await scheduler.GetTrigger(new TriggerKey("legacy-trigger"), cancellationToken);
                Assert.True(trigger.NextFireTimeUtc < DateTimeOffset.UtcNow.AddMinutes(-1));
                Assert.Equal(SimpleTriggerMisfireInstruction.FireNow, Assert.IsAssignableFrom<ISimpleTrigger>(trigger).MisfireInstruction);
                await scheduler.Start(cancellationToken);
                Assert.Equal("Hello from Quartz 3", await delivery.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
            }
            finally { await system.Terminate(); }
        }

        [Fact]
        public async Task Failed_Database_Write_Should_Return_Failure_And_Save_No_Job_Or_Trigger()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var store = await Store.Create(migrate: true);
            await store.Execute("CREATE TRIGGER reject_job BEFORE INSERT ON QRTZ_JOB_DETAILS BEGIN SELECT RAISE(ABORT, 'test write failure'); END;");
            await using var factory = QuartzSchedulerBuilder.Create().UseProperties(store.Properties()).Build();
            var scheduler = await factory.GetScheduler(cancellationToken);
            var system = ActorSystem.Create("write-failure-test");
            try
            {
                var actor = system.ActorOf(Props.Create(() => new QuartzPersistentActor(scheduler)));
                var trigger = TriggerBuilder.Create().WithIdentity("failed-trigger").ForJob("failed-job").StartNow().Build();
                var response = await actor.Ask<object>(new CreatePersistentJob(actor.Path, "message", trigger), TimeSpan.FromSeconds(5), cancellationToken);
                Assert.NotNull(Assert.IsType<CreateJobFail>(response).Reason);
                Assert.False(await scheduler.Exists(new JobKey("failed-job"), cancellationToken));
                Assert.False(await scheduler.Exists(new TriggerKey("failed-trigger"), cancellationToken));
            }
            finally { await system.Terminate(); }
        }

        [Fact]
        public async Task Unmigrated_Quartz3_Store_Should_Refuse_Quartz4_Startup()
        {
            await using var store = await Store.Create(migrate: false);
            await using var factory = QuartzSchedulerBuilder.Create().UseProperties(store.Properties()).Build();
            await Assert.ThrowsAsync<global::Quartz.SchedulerException>(async () => await factory.GetScheduler(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Audit_Should_Read_Legacy_Json_And_Report_Invalid_Cron_Without_Modifying_Store()
        {
            await using var store = await Store.Create(migrate: false);
            foreach (var name in new[] { "good", "bad" })
                await store.Execute($"INSERT INTO QRTZ_TRIGGERS SELECT SCHED_NAME,'{name}',TRIGGER_GROUP,JOB_NAME,JOB_GROUP,DESCRIPTION,NEXT_FIRE_TIME,PREV_FIRE_TIME,PRIORITY,TRIGGER_STATE,'CRON',START_TIME,END_TIME,CALENDAR_NAME,MISFIRE_INSTR,JOB_DATA FROM QRTZ_TRIGGERS WHERE TRIGGER_NAME='legacy-trigger'");
            await store.Execute("INSERT INTO QRTZ_CRON_TRIGGERS VALUES ('QuartzScheduler','good','DEFAULT','0 0 9 ? * MON','UTC');"
                + "INSERT INTO QRTZ_CRON_TRIGGERS VALUES ('QuartzScheduler','bad','DEFAULT','0 0 9 ? * MON/2','UTC');");
            await using var connection = new SqliteConnection(store.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var report = await StoreAudit.InspectAsync(connection, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, report.CronTriggers);
            Assert.Equal(1, report.Blobs);
            Assert.Contains("bad", Assert.Single(report.Issues).Location);
            Assert.Contains("MON/2", report.Issues[0].Reason);
            Assert.Equal("0 0 9 ? * MON/2", await Scalar(connection, "SELECT CRON_EXPRESSION FROM QRTZ_CRON_TRIGGERS WHERE TRIGGER_NAME='bad'"));
        }

        [Fact]
        public async Task Audit_Should_Report_Binary_Blob_And_Reject_Missing_Schema()
        {
            await using var store = await Store.Create(migrate: false);
            await store.Execute("UPDATE QRTZ_JOB_DETAILS SET JOB_DATA=x'00010000';");
            await using var connection = new SqliteConnection(store.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var report = await StoreAudit.InspectAsync(connection, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("Binary blob", Assert.Single(report.Issues).Reason);
            await Assert.ThrowsAsync<SqliteException>(() => StoreAudit.InspectAsync(connection, "MISSING_", cancellationToken: TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Binary_Helper_Cli_Should_Convert_Offline_And_Quartz4_Should_Deliver_The_Legacy_Job()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var store = await Store.Create(migrate: false);
            await store.Execute(await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "quartz3_binary_blobs.sql"), cancellationToken));
            var original = await store.JobBlob();
            var wrongConverterScope = await RunHelper("Quartz3Migration", "--database", store.Path, "--trusted-backup", "--scheduler", "typo", "--apply", "--schedulers-stopped");
            Assert.Equal(1, wrongConverterScope.ExitCode);
            Assert.Contains("No stored rows match", wrongConverterScope.Output);
            Assert.Equal(original, await store.JobBlob());
            var wrongAuditScope = await RunHelper("Akka.Quartz.Actor.Upgrade", "--database", store.Path, "--scheduler", "typo");
            Assert.Equal(1, wrongAuditScope.ExitCode);
            Assert.Contains("No stored rows match", wrongAuditScope.Output);
            var rejected = await RunHelper("Quartz3Migration", "--database", store.Path, "--apply", "--schedulers-stopped");
            Assert.Equal(1, rejected.ExitCode); // No trusted-backup acknowledgement.
            Assert.Equal(original, await store.JobBlob());
            var rejectedLive = await RunHelper("Quartz3Migration", "--database", store.Path, "--trusted-backup", "--apply");
            Assert.Equal(1, rejectedLive.ExitCode); // No stopped-schedulers acknowledgement.
            Assert.Equal(original, await store.JobBlob());
            var preview = await RunHelper("Quartz3Migration", "--database", store.Path, "--trusted-backup");
            Assert.Equal(0, preview.ExitCode);
            Assert.Equal(3, JsonDocument.Parse(preview.Output).RootElement.GetProperty("BinaryBlobs").GetInt32());
            Assert.Equal(original, await store.JobBlob());
            var preAudit = await RunHelper("Akka.Quartz.Actor.Upgrade", "--database", store.Path);
            Assert.Equal(1, preAudit.ExitCode);
            var applied = await RunHelper("Quartz3Migration", "--database", store.Path, "--trusted-backup", "--apply", "--schedulers-stopped");
            Assert.Equal(0, applied.ExitCode);
            var audit = await RunHelper("Akka.Quartz.Actor.Upgrade", "--database", store.Path);
            Assert.Equal(0, audit.ExitCode);
            Assert.Empty(JsonDocument.Parse(audit.Output).RootElement.GetProperty("Issues").EnumerateArray());
            await store.Execute(await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "schema_30_to_40_upgrade_sqlite.sql"), cancellationToken));
            var nearFuture = DateTimeOffset.UtcNow.AddSeconds(1).UtcDateTime.Ticks;
            await store.Execute($"UPDATE QRTZ_TRIGGERS SET NEXT_FIRE_TIME={nearFuture}, START_TIME={nearFuture}");
            await using var factory = QuartzSchedulerBuilder.Create().UseProperties(store.Properties()).Build();
            var scheduler = await factory.GetScheduler(cancellationToken);
            Assert.IsType<HolidayCalendar>(await scheduler.GetCalendar("holiday", cancellationToken));
            Assert.Equal(123L, Convert.ToInt64((await scheduler.GetTrigger(new TriggerKey("legacy-trigger"), cancellationToken)).JobDataMap["count"]));
            var system = ActorSystem.Create("compat-test");
            var delivery = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                system.ActorOf(Props.Create(() => new LegacyReceiver(delivery)), "receiver");
                var actor = system.ActorOf(Props.Create(() => new QuartzPersistentActor(scheduler)), "quartz");
                await actor.Ask<ActorIdentity>(new Identify(null), TimeSpan.FromSeconds(5), cancellationToken);
                await scheduler.Start(cancellationToken);
                Assert.Equal("Hello from Quartz 3", await delivery.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
            }
            finally { await system.Terminate(); }
        }

        private static Task<(int ExitCode, string Output)> RunHelper(string name, params string[] arguments) =>
            QuartzUpgradeTests.HelperProcess.Run(name, arguments);

        private static async Task<object> Scalar(SqliteConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        public sealed record ScheduledEnvelope(string Name, int Number);
        private sealed class Receiver : ReceiveActor
        {
            public Receiver(TaskCompletionSource<ScheduledEnvelope> delivery) => Receive<ScheduledEnvelope>(message => delivery.TrySetResult(message));
        }
        private sealed class LegacyReceiver : ReceiveActor
        {
            public LegacyReceiver(TaskCompletionSource<string> delivery) => Receive<string>(message => delivery.TrySetResult(message));
        }

        private sealed class Store : IAsyncDisposable
        {
            private readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"quartz-upgrade-safety-{Guid.NewGuid():N}.db");
            public string Path => _path;
            public string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();
            public static async Task<Store> Create(bool migrate)
            {
                var store = new Store();
                try
                {
                    await store.Execute(await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "quartz3_legacy_job.sql"), TestContext.Current.CancellationToken));
                    if (migrate) await store.Execute(await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "schema_30_to_40_upgrade_sqlite.sql"), TestContext.Current.CancellationToken));
                    return store;
                }
                catch { await store.DisposeAsync(); throw; }
            }
            public async Task<byte[]> JobBlob()
            {
                await using var connection = new SqliteConnection(ConnectionString);
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                return (byte[])await Scalar(connection, "SELECT JOB_DATA FROM QRTZ_JOB_DETAILS");
            }
            public async Task Execute(string sql)
            {
                await using var connection = new SqliteConnection(ConnectionString);
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            public NameValueCollection Properties(string scheduler = "QuartzScheduler") => new()
            {
                ["quartz.scheduler.instanceName"] = scheduler,
                ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.LocalTransactionJobStore, Quartz",
                ["quartz.jobStore.useProperties"] = "false",
                ["quartz.jobStore.dataSource"] = "default",
                ["quartz.jobStore.tablePrefix"] = "QRTZ_",
                ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.SQLiteDelegate, Quartz",
                ["quartz.jobStore.misfireThreshold"] = "1000",
                ["quartz.dataSource.default.provider"] = "SQLite-Microsoft",
                ["quartz.dataSource.default.connectionString"] = ConnectionString,
                ["quartz.serializer.type"] = "newtonsoft"
            };
            public ValueTask DisposeAsync()
            {
                File.Delete(_path);
                return ValueTask.CompletedTask;
            }
        }
    }
}
