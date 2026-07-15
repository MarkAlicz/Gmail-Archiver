


using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

using MimeKit;

using System.Security.Cryptography;
using System.Text.Json;



namespace IVolt.Products.GmailArchiver
{
	////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>	A program. </summary>
	///
	/// <remarks>	I Volt, 5/4/2026. </remarks>
	////////////////////////////////////////////////////////////////////////////////////////////////////

	internal class Program
	{
		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Main entry-point for this application. </summary>
		///
		/// <remarks>	I Volt, 5/4/2026. </remarks>
		///
		/// <param name="args">	An array of command-line argument strings. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		static void Main(string[] args)
		{
			const string GmailImapHost = "imap.gmail.com";
			const int GmailImapPort = 993;

			Console.Write("Gmail address: ");
			string email = Console.ReadLine()!.Trim();

			Console.Write("App password: ");
			string password = ReadPassword();

			string backupRoot = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.Desktop)				
			);

		backupDirectory:

			Console.WriteLine("Enter the Backup Root Directory (or press Enter to use default):");
			Console.WriteLine("  Default: " + backupRoot);
			string newbackupRoot = Console.ReadLine()!.Trim();

			if (!string.IsNullOrWhiteSpace(newbackupRoot))
			{
				if (Directory.Exists(newbackupRoot))
				{
					backupRoot = newbackupRoot;
				}
				else
				{
					Console.WriteLine("Directory does not exist. (Press Any Key To Retry)");
					Console.ReadKey();
					Console.WriteLine("");
					goto backupDirectory;
				}
			}

			backupRoot= Path.Combine(backupRoot, "GmailBackup_" + Sanitize(email));

			Directory.CreateDirectory(backupRoot);

			using var client = new ImapClient();

			client.ServerCertificateValidationCallback = (s, c, h, e) => true;
			client.Connect(GmailImapHost, GmailImapPort, SecureSocketOptions.SslOnConnect);
			client.Authenticate(email, password);

			Console.WriteLine();
			Console.WriteLine("Connected. Backing up all folders...");

			foreach (var folder in client.GetFolders(client.PersonalNamespaces[0]))
			{
				BackupFolder(folder, backupRoot);
			}

			client.Disconnect(true);

			Console.WriteLine();
			Console.WriteLine($"Backup complete: {backupRoot}");

			void BackupFolder(IMailFolder folder, string root)
			{
				try
				{
					folder.Open(FolderAccess.ReadOnly);
				}
				catch
				{
					return;
				}

				string folderPath = Path.Combine(root, Sanitize(folder.FullName));
				Directory.CreateDirectory(folderPath);

				string stateFile = Path.Combine(folderPath, "_backup_state.json");
				var state = LoadState(stateFile);

				Console.WriteLine($"Folder: {folder.FullName} | Messages: {folder.Count}");

				var uids = folder.Search(SearchQuery.All);

				foreach (var uid in uids)
				{
					if (state.DownloadedUids.Contains(uid.Id))
						continue;

					try
					{
						var message = folder.GetMessage(uid);

						string subject = string.IsNullOrWhiteSpace(message.Subject)
							? "No Subject"
							: message.Subject;

						string date = message.Date.UtcDateTime.ToString("yyyyMMdd_HHmmss");
						string fileName = $"{date}_{uid.Id}_{Sanitize(subject)}.eml";

						if (fileName.Length > 180)
							fileName = fileName[..180] + ".eml";

						string filePath = Path.Combine(folderPath, fileName);

						using (var stream = File.Create(filePath))
						{
							message.WriteTo(stream);
						}

						string hash = Sha256File(filePath);

						File.WriteAllText(
							Path.ChangeExtension(filePath, ".json"),
							JsonSerializer.Serialize(new
							{
								Folder = folder.FullName,
								Uid = uid.Id,
								Subject = message.Subject,
								From = message.From.ToString(),
								To = message.To.ToString(),
								Cc = message.Cc.ToString(),
								Date = message.Date,
								MessageId = message.MessageId,
								Sha256 = hash
							}, new JsonSerializerOptions { WriteIndented = true })
						);

						state.DownloadedUids.Add(uid.Id);
						SaveState(stateFile, state);

						Console.WriteLine($"  Saved UID {uid.Id}");
					}
					catch (Exception ex)
					{
						Console.WriteLine($"  Failed UID {uid.Id}: {ex.Message}");
					}
				}
			}

			static BackupState LoadState(string path)
			{
				if (!File.Exists(path))
					return new BackupState();

				return JsonSerializer.Deserialize<BackupState>(File.ReadAllText(path))
					   ?? new BackupState();
			}

			static void SaveState(string path, BackupState state)
			{
				File.WriteAllText(
					path,
					JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true })
				);
			}

			static string Sanitize(string value)
			{
				foreach (char c in Path.GetInvalidFileNameChars())
					value = value.Replace(c, '_');

				value = value.Replace('/', '_').Replace('\\', '_').Trim();

				return string.IsNullOrWhiteSpace(value) ? "Unnamed" : value;
			}

			static string Sha256File(string path)
			{
				using var sha = SHA256.Create();
				using var stream = File.OpenRead(path);
				return Convert.ToHexString(sha.ComputeHash(stream));
			}

			static string ReadPassword()
			{
				var pass = "";
				ConsoleKeyInfo key;

				while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
				{
					if (key.Key == ConsoleKey.Backspace && pass.Length > 0)
					{
						pass = pass[..^1];
						Console.Write("\b \b");
					}
					else if (!char.IsControl(key.KeyChar))
					{
						pass += key.KeyChar;
						Console.Write("*");
					}
				}

				Console.WriteLine();
				return pass;
			}
		}
	}

	////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>	A backup state. This class cannot be inherited. </summary>
	///
	/// <remarks>	I Volt, 5/4/2026. </remarks>
	////////////////////////////////////////////////////////////////////////////////////////////////////

	public sealed class BackupState
	{
		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Gets or sets the downloaded uids. </summary>
		///
		/// <value>	The downloaded uids. </value>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public HashSet<uint> DownloadedUids { get; set; } = new();
	}
}
