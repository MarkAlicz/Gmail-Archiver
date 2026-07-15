
using System;
using System.IO;
using System.Security.Cryptography;   // DPAPI — Windows only
using System.Text;

using IVolt.Core.Email.Configuration; // Configuration_Definition_Container, Server, Performance

namespace IVolt.Core.Email.Gmail
{
	////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// Console lifecycle for Gmail IMAP configurations. Prompts for an email, locates Resources\
	/// Configurations\{sanitized}.json, DPAPI-unprotects (CurrentUser), and deserializes into a
	/// Configuration_Definition_Container. If none exists, interactively builds one and saves it
	/// DPAPI-protected. Windows-only (DPAPI).
	/// </summary>
	///
	/// <remarks>	I Volt, 6/30/2026. </remarks>
	////////////////////////////////////////////////////////////////////////////////////////////////////

	public static class Gmail_IMAP_Management_Class
	{
		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Per-user configuration directory (portable on Windows, ~/.config on Unix). </summary>
		///
		/// <value>	The pathname of the configuration directory. </value>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string ConfigDirectory => App_Paths.ConfigDirectory;

		// =====================================================================
		// Public entry point
		// =====================================================================

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Interactive load-or-create. Returns a decrypted, loaded config. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <exception cref="CryptographicException">	Thrown when a Cryptographic error condition
		/// 											occurs. </exception>
		///
		/// <returns>	A Configuration_Definition_Container. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static Configuration_Definition_Container Run()
		{
			string email = PromptRequired("Email address");
			string sanitized = SanitizeEmail(email);                    // markalicz@gmail.com -> markalicz_gmail_com
			string path = Path.Combine(ConfigDirectory, sanitized + ".json");

			if (File.Exists(path))
			{
				Console.WriteLine($"Found configuration: {path}");
				try
				{
					string json = UnprotectFile(path);
					var cfg = Configuration_Definition_Container.FromJson(json);
					Console.WriteLine("Configuration loaded and decrypted.");
					return cfg;
				}
				catch (CryptographicException ex)
				{
					// DPAPI fails if the file was protected by a different user/machine.
					Console.WriteLine($"Failed to decrypt '{path}': {ex.Message}");
					Console.WriteLine("This file was likely protected under a different Windows account.");
					throw;
				}
			}

			Console.WriteLine($"No configuration found for '{email}'.");
			var created = PromptForNewConfiguration(email, sanitized);
			SaveProtected(created, path);
			Console.WriteLine($"Saved (DPAPI-protected, CurrentUser): {path}");
			return created;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// "Continue" path: opens an EXISTING configuration only. Prompts for the email, requires the
		/// file to be present, DPAPI-decrypts, validates, and returns it — or returns null with guidance
		/// if there is nothing to open.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/3/2026. </remarks>
		///
		/// <returns>	The loaded configuration, or null. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static Configuration_Definition_Container RunOpenExisting()
		{
			string email = PromptRequired("Email address");
			string path = PathFor(email);
			if (!File.Exists(path))
			{
				Console.WriteLine($"  No configuration found for '{email}'.");
				Console.WriteLine("  Choose 'New configuration' to create one first.");
				return null;
			}

			Configuration_Definition_Container cfg;
			try
			{
				cfg = Configuration_Definition_Container.FromJson(UnprotectFile(path));
			}
			catch (Exception ex)   // CryptographicException, PlatformNotSupportedException (cross-OS DPAPI), etc.
			{
				Console.WriteLine($"  Failed to decrypt '{path}': {ex.Message}");
				Console.WriteLine("  It was likely protected under a different account, machine, or OS.");
				Console.WriteLine("  Recreate it here with 'New configuration'.");
				return null;
			}

			WarnOnValidationIssues(cfg);
			Console.WriteLine("  Configuration loaded and decrypted.");
			return cfg;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// "New" path: force-creates a configuration. Prompts for the email; if one already exists the
		/// operator is asked before overwriting it. Returns the created (and saved) configuration, or
		/// null if the operator declined to overwrite.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/3/2026. </remarks>
		///
		/// <returns>	The created configuration, or null. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static Configuration_Definition_Container RunCreateNew()
		{
			string email = PromptRequired("Email address");
			string sanitized = SanitizeEmail(email);
			string path = PathFor(email);

			if (File.Exists(path))
			{
				Console.Write($"  A configuration for '{email}' already exists. Overwrite it? (y/N): ");
				string a = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
				if (a != "y" && a != "yes")
				{
					Console.WriteLine("  Keeping the existing configuration; opening it instead.");
					try { return Configuration_Definition_Container.FromJson(UnprotectFile(path)); }
					catch (Exception ex) { Console.WriteLine($"  Could not open it: {ex.Message}"); return null; }
				}
			}

			var created = PromptForNewConfiguration(email, sanitized);
			WarnOnValidationIssues(created);
			SaveProtected(created, path);
			Console.WriteLine($"  Saved (DPAPI-protected, CurrentUser): {path}");
			return created;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Prints any validation problems without blocking use. </summary>
		///
		/// <remarks>	I Volt, 7/3/2026. </remarks>
		///
		/// <param name="cfg">	The configuration. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static void WarnOnValidationIssues(Configuration_Definition_Container cfg)
		{
			var problems = cfg?.Validate();
			if (problems == null || problems.Count == 0) return;
			Console.WriteLine("  ! Configuration warnings:");
			foreach (var p in problems) Console.WriteLine($"      - {p}");
			Console.WriteLine("    Use Operations -> 'Edit Configuration File' to fix these.");
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Non-interactive load. Throws if the file is missing. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <exception cref="FileNotFoundException">	Thrown when the requested file is not present. </exception>
		///
		/// <param name="email">	The email. </param>
		///
		/// <returns>	A Configuration_Definition_Container. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static Configuration_Definition_Container Load(string email)
		{
			string path = PathFor(email);
			if (!File.Exists(path))
				throw new FileNotFoundException($"No configuration for '{email}'.", path);
			return Configuration_Definition_Container.FromJson(UnprotectFile(path));
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Serialize + DPAPI-protect + write for the given config's email. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <exception cref="InvalidOperationException">	Thrown when the requested operation is
		/// 												invalid. </exception>
		///
		/// <param name="cfg">	The configuration. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static void Save(Configuration_Definition_Container cfg)
		{
			if (string.IsNullOrWhiteSpace(cfg?.email))
				throw new InvalidOperationException("Config has no email; cannot derive path.");
			SaveProtected(cfg, PathFor(cfg.email));
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// ===================================================================== FailurePath +
		/// sanitization
		/// =====================================================================.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="email">	The email. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static string PathFor(string email) =>
			Path.Combine(ConfigDirectory, SanitizeEmail(email) + ".json");

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Lowercase, then replace every '@' and '.' with '_'. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <exception cref="ArgumentException">	Thrown when one or more arguments have unsupported or
		/// 										illegal values. </exception>
		///
		/// <param name="email">	The email. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static string SanitizeEmail(string email)
		{
			if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Empty email.", nameof(email));
			var sb = new StringBuilder(email.Length);
			foreach (char ch in email.Trim().ToLowerInvariant())
				sb.Append(ch is '@' or '.' ? '_' : ch);
			return sb.ToString();
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// ===================================================================== DPAPI whole-file
		/// protect / unprotect
		/// =====================================================================.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="path">	Full pathname of the file. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static string UnprotectFile(string path)
		{
			string stored = File.ReadAllText(path, Encoding.UTF8);
			byte[] plain = Secret_Protector.Unprotect(stored);
			return Encoding.UTF8.GetString(plain);
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Protect to file. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="json">	The JSON. </param>
		/// <param name="path">	Full pathname of the file. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static void ProtectToFile(string json, string path)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			string stored = Secret_Protector.Protect(Encoding.UTF8.GetBytes(json));
			File.WriteAllText(path, stored, Encoding.UTF8);
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Saves a protected. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="cfg"> 	The configuration. </param>
		/// <param name="path">	Full pathname of the file. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static void SaveProtected(Configuration_Definition_Container cfg, string path) =>
			ProtectToFile(cfg.ToJson(), path);

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// ===================================================================== Interactive new-config
		/// wizard
		/// =====================================================================.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="email">		The email. </param>
		/// <param name="sanitized">	The sanitized. </param>
		///
		/// <returns>	A Configuration_Definition_Container. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static Configuration_Definition_Container PromptForNewConfiguration(
			string email, string sanitized)
		{
			Console.WriteLine();
			Console.WriteLine("=== New Gmail configuration ===");
			Console.WriteLine("Press Enter to accept the [default] shown in brackets.");
			Console.WriteLine();

			// Gmail requires an app-specific password when 2FA is on; it doubles as
			// the IMAP/SMTP password. Ask once, reuse across root + server slots.
			string appPassword = PromptSecret("Gmail app-specific password (16 chars)");
			string basePath = Prompt("Base archive path", App_Paths.DefaultArchiveBase);

			long pointerStart = PromptLong("Archive pointer starting increment", 1212);
			long maxPerMin = PromptLong("Max emails per minute", 100);
			long maxAttachMB = PromptLong("Max attachment MB per minute", 50);
			long pauseSec = PromptLong("Pause between requests (seconds)", 11);
			long maxRetries = PromptLong("Max retries", 3);
			long fetchConns = PromptLong("Fetch connections (parallel IMAP, 1-15)", 4);
			long parseWorkers = PromptLong("Parse/store workers (0 = auto = CPU count)", 0);
			string logLevel = Prompt("Log level", "DETAIL");
			string archiveFmt = Prompt("Archive format", "JSON");
			string attachFmt = Prompt("Attachment archive format", "ZIP");
			string archiveStruct = Prompt("Archive structure", "BYMONTH");
			string indexType = Prompt("Index file format type", "IVOLT_SCAN");

			// Resolve archive path tokens now (concrete values persisted).
			string archiveRoot = Path.Combine(basePath, "ARCHIVES", sanitized);
			string indexFolder = Path.Combine(basePath, "INDEX", sanitized, "INDEX") + Path.DirectorySeparatorChar;
			string archiveFolder = archiveRoot + Path.DirectorySeparatorChar;
			string attachFolder = Path.Combine(archiveRoot, "ATTACHMENTS") + Path.DirectorySeparatorChar;

			return new Configuration_Definition_Container
			{
				email = email,
				password = appPassword,                 // Gmail: app password is the login secret
				server = new Server
				{
					imap = "imap.gmail.com",
					port = 993,
					ssl = true,
					application_specific_password = appPassword,
					pop = "pop.gmail.com",
					pop_port = 995,
					pop_ssl = true,
					smtp = "smtp.gmail.com",
					smtp_port = 587,
					smtp_ssl = true,
					smtp_authentication = true,
					smtp_username = email,
					smtp_password = appPassword,
					smtp_application_specific_password = appPassword
				},
				performance = new Performance
				{
					max_emails_per_minute = maxPerMin,
					max_attachment_size_in_MB_per_minute = maxAttachMB,
					pause_between_requests_in_seconds = pauseSec,
					max_retries = maxRetries,
					fetch_connections = fetchConns,
					parse_workers = parseWorkers,
					log_level = logLevel
				},
				archive_index_folder = indexFolder,
				archive_folder = archiveFolder,
				archive_structure = archiveStruct,
				archive_format = archiveFmt,
				archive_attachments = true,
				archive_pointer_starting_increment = pointerStart,
				archive_attachments_format = attachFmt,
				archive_attachments_folder = attachFolder,
				index_fileformat_type = indexType,
				index_email_body = true,
				index_email_subject = true,
				index_email_from = true,
				index_email_to = true,
				index_email_cc = true,
				index_email_bcc = true,
				index_email_date = true,
				index_email_attachments = true
			};
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// ===================================================================== Console prompt helpers
		/// =====================================================================.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="label">	The label. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string PromptRequired(string label)
		{
			while (true)
			{
				Console.Write($"{label}: ");
				string v = (Console.ReadLine() ?? string.Empty).Trim();
				if (!string.IsNullOrEmpty(v)) return v;
				Console.WriteLine("  (required)");
			}
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Prompts. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="label">  	The label. </param>
		/// <param name="default">	The default. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string Prompt(string label, string @default)
		{
			Console.Write($"{label} [{@default}]: ");
			string v = (Console.ReadLine() ?? string.Empty).Trim();
			return string.IsNullOrEmpty(v) ? @default : v;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Prompt long. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="label">  	The label. </param>
		/// <param name="default">	The default. </param>
		///
		/// <returns>	A long. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static long PromptLong(string label, long @default)
		{
			Console.Write($"{label} [{@default}]: ");
			string v = (Console.ReadLine() ?? string.Empty).Trim();
			return long.TryParse(v, out long n) ? n : @default;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Masked input — never echoes the secret to the console. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="label">	The label. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string PromptSecret(string label)
		{
			Console.Write($"{label}: ");
			var sb = new StringBuilder();
			ConsoleKeyInfo k;
			while ((k = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
			{
				if (k.Key == ConsoleKey.Backspace)
				{
					if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
				}
				else if (!char.IsControl(k.KeyChar))
				{
					sb.Append(k.KeyChar);
					Console.Write('*');
				}
			}
			Console.WriteLine();
			return sb.ToString();
		}
	}
}