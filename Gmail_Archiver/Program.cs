
using System;

namespace IVolt.Core.Email.Gmail
{
	////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>	Executable entry point for Gmail_Archiver.exe. </summary>
	///
	/// <remarks>	I Volt, 6/30/2026. </remarks>
	////////////////////////////////////////////////////////////////////////////////////////////////////

	public static class Program
	{
		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Main entry-point for this application. </summary>
		///
		/// <remarks>	I Volt, 7/3/2026. </remarks>
		///
		/// <param name="args">	Command-line arguments. </param>
		///
		/// <returns>	Process exit code — 0 success, non-zero on error. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static int Main(string[] args)
		{
			Console.OutputEncoding = System.Text.Encoding.UTF8;

			// --help / -h is safe on any OS.
			if (HasFlag(args, "--help", "-h", "/?"))
			{
				PrintHelp();
				return 0;
			}

			// Cross-platform: credentials are protected with DPAPI on Windows and an AES key file
			// on Linux/macOS (see Secret_Protector). No OS gate needed.

			// Script mode:  -s / --script  "path/to/script.ias"
			string scriptPath = GetOption(args, "--script", "-s");
			if (scriptPath != null)
			{
				if (scriptPath.Length == 0)
				{
					Console.Error.WriteLine("Missing script path. Usage: Gmail_Archiver -s \"C:\\path\\to\\script.ias\"");
					return 1;
				}
				try { return Script_Runner.Run(scriptPath); }
				catch (Exception ex) { Console.Error.WriteLine("Fatal (script): " + ex); return 1; }
			}

			// Reject unknown options rather than silently starting.
			foreach (var a in args)
			{
				if (a.StartsWith("-"))
				{
					Console.Error.WriteLine($"Unknown option: {a}");
					Console.Error.WriteLine("Run with --help for usage.");
					return 1;
				}
			}

			// Interactive mode.
			try
			{
				Email_Archiver_Engine.Start();
				return 0;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine("Fatal: " + ex);
				return 1;
			}
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	True if any of the given flags is present (case-insensitive). </summary>
		///
		/// <remarks>	I Volt, 7/3/2026. </remarks>
		///
		/// <param name="args"> 	The arguments. </param>
		/// <param name="flags">	The flags to look for. </param>
		///
		/// <returns>	True if present. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static bool HasFlag(string[] args, params string[] flags)
		{
			foreach (var a in args)
				foreach (var f in flags)
					if (string.Equals(a, f, StringComparison.OrdinalIgnoreCase))
						return true;
			return false;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Returns the value following an option (supports "-s value" and "-s=value"), an empty string
		/// if the option is present but has no value, or null if the option is absent.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/3/2026. </remarks>
		///
		/// <param name="args">   	The arguments. </param>
		/// <param name="names">	The option names. </param>
		///
		/// <returns>	The value, empty string, or null. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string GetOption(string[] args, params string[] names)
		{
			for (int i = 0; i < args.Length; i++)
			{
				foreach (var n in names)
				{
					if (string.Equals(args[i], n, StringComparison.OrdinalIgnoreCase))
						return i + 1 < args.Length ? args[i + 1] : string.Empty;
					if (args[i].StartsWith(n + "=", StringComparison.OrdinalIgnoreCase))
						return args[i].Substring(n.Length + 1);
				}
			}
			return null;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Prints usage. </summary>
		///
		/// <remarks>	I Volt, 7/3/2026. </remarks>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static void PrintHelp()
		{
			var fore = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.WriteLine("IVolt Gmail IMAP Archiver");
			Console.ForegroundColor = fore;
			Console.WriteLine();
			Console.WriteLine("Usage:");
			Console.WriteLine("  Gmail_Archiver                 Start the interactive menu.");
			Console.WriteLine("  Gmail_Archiver -s <script>     Run a script non-interactively.");
			Console.WriteLine("  Gmail_Archiver --help          Show this help.");
			Console.WriteLine();
			Console.WriteLine("Options:");
			Console.WriteLine("  -s, --script <path>   Execute the .ias script at <path> (quote paths with spaces).");
			Console.WriteLine("  -h, --help            Show this help and exit.");
			Console.WriteLine();
			Console.WriteLine("First run: you'll be prompted for your Gmail address and a 16-character app");
			Console.WriteLine("password. Generate one at https://myaccount.google.com/apppasswords (requires");
			Console.WriteLine("2-Step Verification). See README.md and Resources\\Docs for details, and");
			Console.WriteLine("Resources\\Examples\\Scripts for a sample script.");
		}
	}
}
