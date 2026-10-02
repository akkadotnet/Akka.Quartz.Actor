using System.Text.Json;
using QuartzUpgradeTools;
using Quartz3Migration;

if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine("Quartz 3 SQL Server/PostgreSQL/SQLite binary-to-Newtonsoft converter (.NET 8). Dry-run by default.");
    Console.WriteLine("--provider sqlite|sqlserver|postgres (--database <file> | --connection-string-env <name>) --trusted-backup [--scheduler <name>] [--prefix QRTZ_]"
        + " [--assembly <application.dll>] [--apply --schedulers-stopped]");
    return 0;
}

try
{
    string? database = null;
    string? connectionStringEnvironment = null;
    var provider = "sqlite";
    string? scheduler = null;
    var prefix = "QRTZ_";
    var apply = false;
    var stopped = false;
    var trusted = false;
    var assemblies = new List<string>();
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
            case "--assembly": assemblies.Add(Path.GetFullPath(Value())); break;
            case "--apply": apply = true; break;
            case "--schedulers-stopped": stopped = true; break;
            case "--trusted-backup": trusted = true; break;
            default: throw new ArgumentException("Unknown option. Use --help.");
        }
    }
    if (!trusted) throw new ArgumentException("Binary deserialization executes application types. Use only a trusted database backup and pass --trusted-backup.");
    if (apply && !stopped) throw new ArgumentException("Stop all schedulers and pass --schedulers-stopped before applying conversion.");
    foreach (var path in assemblies) StoreSchema.LoadApplicationAssembly(path);
    await using var connection = StoreConnection.Create(provider, database, connectionStringEnvironment, readOnly: !apply);
    await connection.OpenAsync();
    var report = await BinaryStoreMigration.ConvertAsync(connection, prefix, scheduler, apply);
    Console.WriteLine(JsonSerializer.Serialize(report));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception is InvalidDataException ? exception.Message
        : "Conversion failed; no transaction committed. " + (exception is ArgumentException ? exception.Message : "Check the database, schema and application assemblies."));
    return 1;
}
