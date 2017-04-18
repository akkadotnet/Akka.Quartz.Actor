using System;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Quartz;
using Xunit;
using System.Data.SQLite;
using System.IO;

namespace Akka.Quartz.Actor.IntegrationTests
{
    public class QuartzPersistentActorIntegration : TestKit.Xunit2.TestKit, IClassFixture<QuartzPersistentActorIntegration.SqliteFixture>
    {
        [Fact]
        public async Task QuartzPersistentActor_DB_Should_Create_Job()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzPersistentActor()), "QuartzActor");
            quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, new { Greeting = "hello" }, TriggerBuilder.Create().WithCronSchedule("*0/5 * * * * ?").Build()));
            ExpectMsg<JobCreated>();
            probe.ExpectMsg(new { Greeting = "hello" }, TimeSpan.FromSeconds(6));
            await Task.Delay(TimeSpan.FromSeconds(5));
            probe.ExpectMsg(new { Greeting = "hello" });
            Sys.Stop(quartzActor);
        }

        private class SqliteFixture : IDisposable
        {
            private const string DatabaseFileName = "quartz-jobs.db";

            public SqliteFixture()
            {
                if (File.Exists(DatabaseFileName))
                {
                    File.Delete(DatabaseFileName);
                }

                SQLiteConnection.CreateFile(DatabaseFileName);
                string script = File.ReadAllText("tables_sqlite.sql");

                using (SQLiteConnection dbConnection = new SQLiteConnection($"Data Source={DatabaseFileName};Version=3;"))
                {
                    using (SQLiteCommand command = new SQLiteCommand(script, dbConnection))
                    {
                        dbConnection.Open();
                        command.ExecuteNonQuery();                        
                    }
                }
            }

            public void Dispose()
            {                
            }
        }

    }
}
