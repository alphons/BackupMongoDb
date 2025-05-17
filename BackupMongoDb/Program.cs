
using BackupMongoDb;

await Helper.BackupAsync(args.Length == 0 ? @"c:\temp" : args[0]);
