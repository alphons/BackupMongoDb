using Alphaleonis.Win32.Vss;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;

namespace BackupMongoDb;

public class Helper
{
	// Methode om te controleren of de applicatie als administrator draait
	private static bool IsRunningAsAdministrator()
	{
		try
		{
			using var identity = WindowsIdentity.GetCurrent();
			var principal = new WindowsPrincipal(identity);
			return principal.IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Error checking administrator privileges: {ex.Message}");
			return false;
		}
	}

	private static async Task<bool> ZipFromSnapshotAsync(string sourceDirectory, string zipPath)
	{
		IVssFactory? vssFactory = null;
		IVssBackupComponents? backup = null;
		try
		{
			// Valideer source directory
			if (!Directory.Exists(sourceDirectory))
			{
				Console.WriteLine($"Source directory '{sourceDirectory}' does not exist.");
				return false;
			}

			// Zorg ervoor dat de doeldirectory bestaat
			string? targetDir = Path.GetDirectoryName(zipPath);
			if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
			{
				Directory.CreateDirectory(targetDir);
			}

			// Verwijder bestaand ZIP-bestand met foutafhandeling
			if (File.Exists(zipPath))
			{
				try
				{
					File.Delete(zipPath);
				}
				catch (IOException ex)
				{
					Console.WriteLine($"Failed to delete existing ZIP file '{zipPath}': {ex.Message}");
					return false;
				}
			}

			// Initialiseer VSS
			vssFactory = VssFactoryProvider.Default.GetVssFactory();
			backup = vssFactory.CreateVssBackupComponents();
			backup.InitializeForBackup(null);
			backup.GatherWriterMetadata();
			backup.SetBackupState(false, true, VssBackupType.Full, false);
			backup.SetContext(VssSnapshotContext.Backup);

			// Bepaal het volume (bijv. C:\)
			string volumePath = Path.GetPathRoot(sourceDirectory)?.TrimEnd('\\') ?? throw new ArgumentException("Could not determine volume for source directory.");
			if (!Directory.Exists(volumePath))
			{
				Console.WriteLine($"Volume '{volumePath}' does not exist.");
				return false;
			}

			volumePath += '\\';

			// Maak een VSS-snapshot van het volume
			Console.WriteLine($"Creating VSS snapshot for volume '{volumePath}' to back up directory '{sourceDirectory}'...");
			Guid setId = backup.StartSnapshotSet();
			Guid snapshotId = backup.AddToSnapshotSet(volumePath);
			backup.PrepareForBackup();

			await Task.Run(() => backup.DoSnapshotSet()); // Asynchroon in een Task

			var snapshotProperties = backup.GetSnapshotProperties(snapshotId);
			var snapshotDevicePath = snapshotProperties.SnapshotDeviceObject;
			var relativePath = sourceDirectory[volumePath.Length..].TrimStart('\\');
			var snapshotSourcePath = Path.Combine(snapshotDevicePath, relativePath);

			Console.WriteLine($"VSS snapshot created successfully");
			// Controleer of het snapshot-pad toegankelijk is
			if (!Directory.Exists(snapshotSourcePath))
			{
				Console.WriteLine($"Snapshot source path '{snapshotSourcePath}' is not accessible.");
				return false;
			}

			// Maak ZIP-bestand van alleen de bestanden in de hoofddirectory van de snapshot
			Console.WriteLine($"Zipping files to '{zipPath}'...");
			using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
			{
				foreach (string file in Directory.GetFiles(snapshotSourcePath, "*", SearchOption.TopDirectoryOnly))
				{
					if (Path.GetExtension(file) == ".lock")
						continue;
					string entryName = Path.GetFileName(file); // Alleen bestandsnaam, geen directorystructuur
					zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
				}
			}
			Console.WriteLine($"ZIP file created successfully");

			backup.DeleteSnapshotSet(setId, false);
			Console.WriteLine($"DeleteSnapshotSet successfully");

			return true;
		}
		catch (VssException ex)
		{
			Console.WriteLine($"VSS error: {ex.Message}");
			return false;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Error zipping '{sourceDirectory}' to '{zipPath}': {ex.Message}");
			return false;
		}
		finally
		{
			// Ruim VSS op
			try
			{
				backup?.BackupComplete();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error completing VSS backup: {ex.Message}");
			}
			backup?.Dispose();
		}
	}

	public static async Task<bool> BackupAsync(string backupDir, string connectionString)
	{
		MongoClient? client;
		IMongoDatabase? database = null;
		bool isLocked = false;

		var sw = Stopwatch.StartNew();

		try
		{
			// Controleer of de applicatie als administrator draait
			if (!IsRunningAsAdministrator())
			{
				Console.WriteLine("This application must be run as an administrator to create VSS snapshots.");
				return false;
			}

			// Valideer backup directory
			if (string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir))
			{
				Console.WriteLine($"Backup directory '{backupDir}' is invalid or does not exist.");
				return false;
			}

			// Maak MongoDB client en database
			client = new MongoClient(connectionString);
			database = client.GetDatabase("admin");

			// Haal de datadirectory op
			var command = new BsonDocument { { "getCmdLineOpts", 1 } };
			var result = await database.RunCommandAsync<BsonDocument>(command);

			string? sourceDirectory = result["parsed"]?["storage"]?["dbPath"]?.AsString;
			if (string.IsNullOrEmpty(sourceDirectory))
			{
				Console.WriteLine("Failed to retrieve MongoDB data directory (dbPath).");
				return false;
			}

			// Vergrendel de database
			Console.WriteLine("Locking the database...");
			var fsyncLockCommand = new BsonDocument { { "fsync", 1 }, { "lock", true } };
			var lockResult = await database.RunCommandAsync<BsonDocument>(fsyncLockCommand);
			if (!lockResult.Contains("ok"))
			{
				Console.WriteLine("Failed to lock database with fsyncLock.");
				return false;
			}
			isLocked = true;

			// Maak backup van alleen de sourceDirectory via VSS-snapshot
			Console.WriteLine("Database locked successfully.");

			Console.WriteLine("Performing backup...");
			string zipFile = Path.Combine(backupDir, $"mongodb_backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

			bool zipSuccess = await ZipFromSnapshotAsync(sourceDirectory, zipFile);

			if (!zipSuccess)
			{
				Console.WriteLine("Backup failed.");
				return false;
			}

			Console.WriteLine($"Backup completed successfully");
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Backup failed: {ex.Message}");
			return false;
		}
		finally
		{
			if (isLocked && database != null)
			{
				try
				{
					Console.WriteLine("Unlocking the database...");
					var fsyncUnlockCommand = new BsonDocument { { "fsyncUnlock", 1 } };
					var unlockResult = await database.RunCommandAsync<BsonDocument>(fsyncUnlockCommand);
					if (!unlockResult.Contains("ok"))
					{
						Console.WriteLine("Failed to unlock database with fsyncLock.");
					}
					Console.WriteLine("Database unlocked successfully");
					Console.WriteLine($"Duration {sw.Elapsed}");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Error unlocking database: {ex.Message}");
				}
			}
		}
	}

	public static async Task<bool> RestoreAsync(string zipFilePath, string connectionString)
	{
		MongoClient? client = null;
		var sw = Stopwatch.StartNew();

		try
		{
			// Controleer of de applicatie als administrator draait
			if (!IsRunningAsAdministrator())
			{
				Console.WriteLine("This application must be run as an administrator to perform a MongoDB restore.");
				return false;
			}

			// Valideer ZIP-bestand
			if (string.IsNullOrWhiteSpace(zipFilePath) || !File.Exists(zipFilePath))
			{
				Console.WriteLine($"ZIP file '{zipFilePath}' is invalid or does not exist.");
				return false;
			}

			// Maak MongoDB-client en haal datadirectory op
			client = new MongoClient(connectionString);
			var database = client.GetDatabase("admin");
			var command = new BsonDocument { { "getCmdLineOpts", 1 } };
			var result = await database.RunCommandAsync<BsonDocument>(command);
			string? dataDirectory = result["parsed"]?["storage"]?["dbPath"]?.AsString;

			if (string.IsNullOrEmpty(dataDirectory))
			{
				Console.WriteLine("Failed to retrieve MongoDB data directory (dbPath).");
				return false;
			}

			// Controleer of de datadirectory bestaat, zo niet, maak deze aan
			if (!Directory.Exists(dataDirectory))
			{
				Directory.CreateDirectory(dataDirectory);
			}

			// Stop de MongoDB-service
			Console.WriteLine("Stopping MongoDB service...");
			using (var serviceController = new ServiceController("MongoDB"))
			{
				if (serviceController.Status != ServiceControllerStatus.Stopped)
				{
					serviceController.Stop();
					serviceController.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
				}
			}
			Console.WriteLine("MongoDB service stopped successfully.");

			// Maak een back-up van de huidige datadirectory (optioneel)
			string backupCurrentDir = Path.Combine(Path.GetDirectoryName(dataDirectory) ?? throw new InvalidOperationException("Invalid data directory path"), $"mongodb_data_backup_{DateTime.Now:yyyyMMdd_HHmmss}");
			if (Directory.Exists(dataDirectory) && Directory.GetFiles(dataDirectory).Length > 0)
			{
				Console.WriteLine($"Backing up current data directory to '{backupCurrentDir}'...");
				Directory.Move(dataDirectory, backupCurrentDir);
				Directory.CreateDirectory(dataDirectory); // Maak lege datadirectory aan
			}

			// Extraheer de ZIP naar de datadirectory
			Console.WriteLine($"Extracting ZIP file '{zipFilePath}' to '{dataDirectory}'...");
			using (var zip = ZipFile.OpenRead(zipFilePath))
			{
				foreach (var entry in zip.Entries)
				{
					string destinationPath = Path.Combine(dataDirectory, entry.FullName);
					// Zorg dat de directorystructuur bestaat
					string? destinationDir = Path.GetDirectoryName(destinationPath);
					if (!string.IsNullOrEmpty(destinationDir))
					{
						Directory.CreateDirectory(destinationDir);
					}
					// Extraheer het bestand
					entry.ExtractToFile(destinationPath, overwrite: true);
				}
			}
			Console.WriteLine("ZIP file extracted successfully.");

			// Stel bestandsrechten in (optioneel, afhankelijk van je setup)
			// Bijv. geef SYSTEM en Administrators volledige rechten
			try
			{
				var directoryInfo = new DirectoryInfo(dataDirectory);
				var security = directoryInfo.GetAccessControl();
				security.AddAccessRule(new FileSystemAccessRule(
					new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
					FileSystemRights.FullControl,
					InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
					PropagationFlags.None,
					AccessControlType.Allow));
				directoryInfo.SetAccessControl(security);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Warning: Failed to set file permissions: {ex.Message}");
			}

			// Start de MongoDB-service
			Console.WriteLine("Starting MongoDB service...");
			using (var serviceController = new ServiceController("MongoDB"))
			{
				serviceController.Start();
				serviceController.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
			}
			Console.WriteLine("MongoDB service started successfully.");

			// Controleer of de database toegankelijk is
			Console.WriteLine("Verifying database access...");
			var dbs = await (await client.ListDatabasesAsync()).ToListAsync();
			Console.WriteLine($"Databases found: {string.Join(", ", dbs.Select(db => db["name"].AsString))}");

			Console.WriteLine($"Restore completed successfully in {sw.Elapsed}");
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Restore failed: {ex.Message}");
			return false;
		}
		finally
		{
			// Zorg dat de MongoDB-service draait, zelfs bij een fout
			try
			{
				using var serviceController = new ServiceController("MongoDB");
				if (serviceController.Status != ServiceControllerStatus.Running)
				{
					serviceController.Start();
					serviceController.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
					Console.WriteLine("MongoDB service restarted after error.");
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error restarting MongoDB service: {ex.Message}");
			}
		}
	}
}
