using System.Text.Json;
using Akka.Quartz.Actor.Upgrade;
using QuartzUpgradeTools;

if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine("Read-only SQL Server/PostgreSQL/SQLite upgrade audit using Quartz 4.0.1 (.NET 10). Does not start a scheduler or modify a store.");
    Console.WriteLine("--provider sqlite|sqlserver|postgres (--database <file> | --connection-string-env <name>) [--scheduler <name>] [--prefix QRTZ_] [--assembly <application.dll>]");
    return 0;
}
try
{
    string? database = null;
    string? connectionStringEnvironment = null;
    var provider = "sqlite";
    string? scheduler = null;
    var prefix = "QRTZ_";
    for (var index = 0; index < args.Length; index++)
    {
        string Value() => ++index < args.Length ? args[index] : throw new ArgumentException("Missing option value.");
        switch (args[index])
        {
            case "--database": database = Value(); break;
            case "--provider": provider = Value(); break;
            case "--connection-string-env": connectionStringEnvironment = Value(); break;
            case "--scheduler": scheduler = Value(); break;
            case "--prefix": prefix = Value(); break;
            case "--assembly": StoreSchema.LoadApplicationAssembly(Value()); break;
            default: throw new ArgumentException("Unknown option. Use --help.");
        }
    }
    await using var connection = StoreConnection.Create(provider, database, connectionStringEnvironment, readOnly: true);
    await connection.OpenAsync();
    var report = await StoreAudit.InspectAsync(connection, prefix, scheduler);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return report.Issues.Count == 0 ? 0 : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception is ArgumentException ? exception.Message
        : "Audit failed: database/schema unreadable. No store changes were made.");
    return 1;
}
