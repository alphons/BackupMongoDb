
using BackupMongoDb;

string connection = args.Length > 2 ? args[2] : "mongodb://localhost:27017";

if (args.Length < 1)
{
	Console.WriteLine("Usage:");
	Console.WriteLine($"\tBackupMongoDb --backup <backupdir> [{connection}]");
	Console.WriteLine($"\tBackupMongoDb --restore <backupfile.zip> [{connection}]");
	Console.WriteLine();
	Console.WriteLine($"\tBackupMongoDb --listshadowcopies");
	Console.WriteLine($"\tBackupMongoDb --deleteshadowcopies");

	Environment.Exit(0);
}

switch(args[0])
{
	case "--backup":
		if (args.Length >= 2)
			await Helper.BackupAsync(args[1], connection);
		else
			Console.WriteLine($"\tBackupMongoDb -backup <backupdir> [{connection}]");
		break;
	case "--restore":
		if (args.Length >= 2)
			await Helper.RestoreAsync(args[1], connection);
		else
			Console.WriteLine($"\tBackupMongoDb -restore <backupfile.zip> [{connection}]");
		break;
	case "--listshadowcopies":
		Helper.ListAllShadowCopies(deleteCopies: false);
		break;
	case "--deleteshadowcopies":
		Helper.ListAllShadowCopies(deleteCopies: true);
		break;
	default:
		Console.WriteLine($"Unknown command: {args[0]}");
		Environment.Exit(1);
		break;
}
