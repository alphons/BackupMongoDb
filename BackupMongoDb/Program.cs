
using BackupMongoDb;

string connection = args.Length > 2 ? args[2] : "mongodb://localhost:27017";

if (args.Length < 2)
{
	Console.WriteLine("Usage:");
	Console.WriteLine($"\tBackupMongoDb -backup <backupdir> [{connection}]");
	Console.WriteLine($"\tBackupMongoDb -restore <backupfile.zip> [{connection}]");

	Environment.Exit(0);
}

if (args[0] == "-backup")
{
	await Helper.BackupAsync(args[1], connection);
}

if (args[0] == "-restore")
{
	await Helper.RestoreAsync(args[1], connection);
}


