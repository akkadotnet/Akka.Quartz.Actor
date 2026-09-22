using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Pattern;
using Microsoft.Data.Sqlite;
using Quartz;
using Xunit;

namespace Akka.Quartz.Actor.IntegrationTests
{
    public class Quartz3UpgradeIntegration
    {
        [Fact]
        public async Task Quartz3_Persisted_Job_Should_Fire_After_Quartz4_Migration()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var databasePath = Path.Combine(Path.GetTempPath(), $"quartz3-upgrade-{Guid.NewGuid():N}.db");

            try
            {
                var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
                await using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync(cancellationToken);
                    await ExecuteScript(connection, "quartz3_legacy_job.sql");
                    // Copied verbatim from quartznet/quartznet v4.0.1, database/migrations/4.0.
                    await ExecuteScript(connection, "schema_30_to_40_upgrade_sqlite.sql");

                    // Preserve the Quartz 3 job and trigger, but bring its saved fire time
                    // near the test clock so the recovered trigger fires promptly.
                    await using var setFireTime = connection.CreateCommand();
                    setFireTime.CommandText = "UPDATE QRTZ_TRIGGERS SET NEXT_FIRE_TIME = $nextFireTime WHERE TRIGGER_NAME = 'legacy-trigger'";
                    setFireTime.Parameters.AddWithValue("$nextFireTime", DateTimeOffset.UtcNow.AddSeconds(2).UtcDateTime.Ticks);
                    Assert.Equal(1, await setFireTime.ExecuteNonQueryAsync(cancellationToken));
                }

                var properties = new NameValueCollection
                {
                    ["quartz.scheduler.instanceName"] = "QuartzScheduler",
                    ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.LocalTransactionJobStore, Quartz",
                    ["quartz.jobStore.useProperties"] = "false",
                    ["quartz.jobStore.dataSource"] = "default",
                    ["quartz.jobStore.tablePrefix"] = "QRTZ_",
                    ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.SQLiteDelegate, Quartz",
                    ["quartz.dataSource.default.provider"] = "SQLite-Microsoft",
                    ["quartz.dataSource.default.connectionString"] = connectionString,
                    ["quartz.serializer.type"] = "newtonsoft"
                };

                await using var factory = QuartzSchedulerBuilder.Create().UseProperties(properties).Build();
                var scheduler = await factory.GetScheduler(cancellationToken);
                var savedJob = await scheduler.GetJobDetail(new JobKey("legacy-job"), cancellationToken);
                Assert.NotNull(savedJob);
                Assert.IsType<byte[]>(savedJob.JobDataMap["message"]);
                var triggerKey = new TriggerKey("legacy-trigger");
                Assert.NotNull(await scheduler.GetTrigger(triggerKey, cancellationToken));

                var delivery = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var actorSystem = ActorSystem.Create("compat-test");
                try
                {
                    actorSystem.ActorOf(Props.Create(() => new Receiver(delivery)), "receiver");
                    var quartzActor = actorSystem.ActorOf(Props.Create(() => new QuartzPersistentActor(scheduler)), "quartz");
                    await quartzActor.Ask<ActorIdentity>(new Identify(null), TimeSpan.FromSeconds(5), cancellationToken);
                    await scheduler.Start(cancellationToken);

                    Assert.Equal("Hello from Quartz 3", await delivery.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
                    var nextFireTime = (await scheduler.GetTrigger(triggerKey, cancellationToken))?.NextFireTimeUtc;
                    Assert.True(nextFireTime > DateTimeOffset.UtcNow, "The recovered repeating trigger should remain scheduled.");
                }
                finally
                {
                    await actorSystem.Terminate();
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                File.Delete(databasePath);
            }
        }

        private static async Task ExecuteScript(SqliteConnection connection, string fileName)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, fileName), TestContext.Current.CancellationToken);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        private sealed class Receiver : ReceiveActor
        {
            public Receiver(TaskCompletionSource<string> delivery)
            {
                Receive<string>(message => delivery.TrySetResult(message));
            }
        }
    }
}
