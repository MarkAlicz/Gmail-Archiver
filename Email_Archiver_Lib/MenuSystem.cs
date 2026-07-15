
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using MailKit;
using MailKit.Net.Imap;

using Newtonsoft.Json;

using IVolt.Core.Email.Configuration;

namespace IVolt.Core.Email.Gmail
{
	////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// Lightweight, extensible console menu framework. Register items with a label and an action;
	/// the menu renders them, reads the choice, and loops until Back/Exit. Items may be
	/// conditionally visible. Add new capabilities by calling Add(...) — no changes to the loop
	/// itself.
	/// 
	/// Usage:
	///   var m = new Menu("OPERATIONS");
	///   m.Add("Edit Configuration", () => Archive_Operations.EditConfiguration());
	///   m.Add("Retry Logged Errors", () => Archive_Operations.RetryLoggedErrors(cfg, store));
	///   m.Show();
	/// </summary>
	///
	/// <remarks>	I Volt, 6/30/2026. </remarks>
	////////////////////////////////////////////////////////////////////////////////////////////////////

	public sealed class Menu
	{
		/// <summary>	(Immutable) the items. </summary>
		private readonly List<Menu_Item> _items = new List<Menu_Item>();
		/// <summary>	(Immutable) the out. </summary>
		private readonly TextWriter _out;
		/// <summary>	(Immutable) the in. </summary>
		private readonly TextReader _in;

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Gets or sets the title. </summary>
		///
		/// <value>	The title. </value>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public string Title { get; set; }

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Gets or sets the back label. </summary>
		///
		/// <value>	The back label. </value>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public string BackLabel { get; set; } = "Back";

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Constructor. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="title"> 	The title. </param>
		/// <param name="output">	(Optional) The output. </param>
		/// <param name="input"> 	(Optional) The input. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public Menu(string title, TextWriter output = null, TextReader input = null)
		{
			Title = title;
			_out = output ?? Console.Out;
			_in = input ?? Console.In;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Register a menu item. Returns this for fluent chaining. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="label">  	The label. </param>
		/// <param name="action"> 	The action. </param>
		/// <param name="visible">	(Optional) The visible. </param>
		///
		/// <returns>	A Menu. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public Menu Add(string label, Action action, Func<bool> visible = null)
		{
			_items.Add(new Menu_Item { Label = label, Action = action, Visible = visible });
			return this;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Register an item whose action reports success/failure for a status line. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="label">  	The label. </param>
		/// <param name="action"> 	The action. </param>
		/// <param name="visible">	(Optional) The visible. </param>
		///
		/// <returns>	A Menu. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public Menu Add(string label, Func<bool> action, Func<bool> visible = null)
		{
			_items.Add(new Menu_Item
			{
				Label = label,
				Action = () =>
				{
					bool ok = action();
					_out.WriteLine(ok ? "  ✓ Done." : "  ! Completed with issues.");
				},
				Visible = visible
			});
			return this;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Renders the menu and processes input until the user chooses Back/Exit (0). </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public void Show()
		{
			while (true)
			{
				var visible = _items.Where(i => i.Visible == null || i.Visible()).ToList();

				_out.WriteLine();
				_out.WriteLine($"  ===== {Title} =====");
				for (int i = 0;i < visible.Count;i++)
					_out.WriteLine($"  {i + 1}. {visible[i].Label}");
				_out.WriteLine($"  0. {BackLabel}");
				_out.Write("  Choose: ");

				string raw = (_in.ReadLine() ?? string.Empty).Trim();
				if (raw == "0") return;

				if (!int.TryParse(raw, out int sel) || sel < 1 || sel > visible.Count)
				{
					_out.WriteLine("  Unrecognized option.");
					continue;
				}

				try
				{
					visible[sel - 1].Action();
				}
				catch (Exception ex)
				{
					_out.WriteLine($"  ✗ '{visible[sel - 1].Label}' failed: {ex.Message}");
				}
			}
		}
	}

	////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>	A single registered menu entry. </summary>
	///
	/// <remarks>	I Volt, 7/1/2026. </remarks>
	////////////////////////////////////////////////////////////////////////////////////////////////////

	public sealed class Menu_Item
	{
		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Gets or sets the label. </summary>
		///
		/// <value>	The label. </value>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public string Label { get; set; }

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Gets or sets the action. </summary>
		///
		/// <value>	The action. </value>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public Action Action { get; set; }

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Optional predicate; when it returns false the item is hidden. </summary>
		///
		/// <value>	A function delegate that yields a bool. </value>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public Func<bool> Visible { get; set; }
	}

	////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// Persistent per-account record of messages that failed to archive. Written by the fetch loop
	/// when it gives up on a UID, read by the "Retry Logged Errors" menu item. Stored as JSON lines
	/// next to the archive so it survives restarts and is human-inspectable.
	/// </summary>
	///
	/// <remarks>	I Volt, 7/1/2026. </remarks>
	////////////////////////////////////////////////////////////////////////////////////////////////////

	public sealed class Failure_Log
	{
		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	An entry. This class cannot be inherited. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public sealed class Entry
		{
			////////////////////////////////////////////////////////////////////////////////////////////////////
			/// <summary>	Gets or sets the UID. </summary>
			///
			/// <value>	The UID. </value>
			////////////////////////////////////////////////////////////////////////////////////////////////////

			[JsonProperty("uid")] public uint uid { get; set; }

			////////////////////////////////////////////////////////////////////////////////////////////////////
			/// <summary>	Gets or sets the UID validity. </summary>
			///
			/// <value>	The UID validity. </value>
			////////////////////////////////////////////////////////////////////////////////////////////////////

			[JsonProperty("uid_validity")] public uint uid_validity { get; set; }

			////////////////////////////////////////////////////////////////////////////////////////////////////
			/// <summary>	Gets or sets the pathname of the folder. </summary>
			///
			/// <value>	The pathname of the folder. </value>
			////////////////////////////////////////////////////////////////////////////////////////////////////

			[JsonProperty("folder")] public string folder { get; set; }

			////////////////////////////////////////////////////////////////////////////////////////////////////
			/// <summary>	Gets or sets the reason. </summary>
			///
			/// <value>	The reason. </value>
			////////////////////////////////////////////////////////////////////////////////////////////////////

			[JsonProperty("reason")] public string reason { get; set; }

			////////////////////////////////////////////////////////////////////////////////////////////////////
			/// <summary>	Gets or sets the UTC. </summary>
			///
			/// <value>	The UTC. </value>
			////////////////////////////////////////////////////////////////////////////////////////////////////

			[JsonProperty("utc")] public string utc { get; set; }

			////////////////////////////////////////////////////////////////////////////////////////////////////
			/// <summary>	Gets or sets a value indicating whether the resolved. </summary>
			///
			/// <value>	True if resolved, false if not. </value>
			////////////////////////////////////////////////////////////////////////////////////////////////////

			[JsonProperty("resolved")] public bool resolved { get; set; }
		}

		/// <summary>	(Immutable) full pathname of the file. </summary>
		private readonly string _path;

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Constructor. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="archiveFolder">	Pathname of the archive folder. </param>
		/// <param name="accountKey">   	The account key. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public Failure_Log(string archiveFolder, string accountKey)
		{
			_path = Path.Combine(archiveFolder, accountKey + ".failures.log");
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Gets the full pathname of the failure file. </summary>
		///
		/// <value>	The full pathname of the failure file. </value>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public string FailurePath => _path;

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Append a failure. Called by the fetch loop's give-up branch. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="uid">		  	The UID. </param>
		/// <param name="uidValidity">	The UID validity. </param>
		/// <param name="folder">	  	Pathname of the folder. </param>
		/// <param name="reason">	  	The reason. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public void Record(uint uid, uint uidValidity, string folder, string reason)
		{
			var e = new Entry
			{
				uid = uid,
				uid_validity = uidValidity,
				folder = folder,
				reason = reason,
				utc = DateTimeOffset.UtcNow.ToString("o"),
				resolved = false
			};
			File.AppendAllText(_path, JsonConvert.SerializeObject(e) + Environment.NewLine);
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Reads all entries (one JSON object per line). Tolerant of blank/garbled lines.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <returns>	all. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public List<Entry> ReadAll()
		{
			var list = new List<Entry>();
			if (!File.Exists(_path)) return list;
			foreach (var line in File.ReadAllLines(_path))
			{
				if (string.IsNullOrWhiteSpace(line)) continue;
				try { var e = JsonConvert.DeserializeObject<Entry>(line); if (e != null) list.Add(e); }
				catch { /* skip unparseable line */ }
			}
			return list;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Distinct unresolved UIDs, most recent reason kept. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <returns>	A List&lt;Entry&gt; </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public List<Entry> UnresolvedDistinct()
		{
			return ReadAll()
				.Where(e => !e.resolved)
				.GroupBy(e => e.uid)
				.Select(g => g.Last())
				.OrderBy(e => e.uid)
				.ToList();
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Rewrites the log marking the given UIDs resolved. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="uids">	The uids. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public void MarkResolved(IEnumerable<uint> uids)
		{
			var set = new HashSet<uint>(uids);
			var all = ReadAll();
			foreach (var e in all) if (set.Contains(e.uid)) e.resolved = true;
			using var w = new StreamWriter(_path, false);
			foreach (var e in all) w.WriteLine(JsonConvert.SerializeObject(e));
		}
	}

	////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// The two fully-coded operations exposed through the menu subsystem:
	///   (1) Edit a configuration file — DPAPI-load, field-by-field edit, DPAPI-save. (2) Retry
	///   logged errors — re-fetch and re-process every unresolved UID from the failure log.
	/// Wire these into any Menu via Add(...). More items can be added the same way.
	/// </summary>
	///
	/// <remarks>	I Volt, 7/1/2026. </remarks>
	////////////////////////////////////////////////////////////////////////////////////////////////////

	public static class Archive_Operations
	{
		/// <summary>	(Immutable) the out. </summary>
		private static readonly TextWriter Out = Console.Out;

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Convenience: build the operations submenu with both items registered. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="cfg">  	The configuration. </param>
		/// <param name="store">	The store. </param>
		///
		/// <returns>	A Menu. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static Menu BuildMenu(Configuration_Definition_Container cfg, Archive_Store store)
		{
			var menu = new Menu("OPERATIONS");
			menu.Add("Verify Files (resume readiness)", () => VerifyFiles(store));
			menu.Add("Edit Configuration File", () => EditConfiguration(cfg?.email));
			menu.Add("Retry Errors Found in Logs", () => RetryLoggedErrors(cfg, store));
			menu.Add("Analyze Recovery / Continuation", () => Email_Archiver_Engine.ShowContinuation(store));
			menu.Add("Rebuild Manifest From Tree", () => RebuildManifest(store));
			menu.Add("Rebuild Search Index From Tree", () => RebuildIndex(store));
			return menu;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Runs the file-level verification pass and renders the confidence verdict. Offers deep mode
		/// (opens every record + attachment ZIP) for a thorough check.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/3/2026. </remarks>
		///
		/// <param name="store">	The store. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static void VerifyFiles(Archive_Store store)
		{
			if (store == null) { Out.WriteLine("  No active configuration."); return; }
			bool deep = Confirm("  Deep verify (open every record + attachment ZIP)? Slower on big archives");
			Out.WriteLine(deep ? "  Running DEEP verification …" : "  Running QUICK verification …");
			var report = store.VerifyFiles(deep, (done, total) => ReportProgress("records checked", done, total));
			Out.WriteLine();
			report.Render(Out);
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Rebuilds the manifest from the record tree and reports the reconciled state. </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		///
		/// <param name="store">	The store. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static void RebuildManifest(Archive_Store store)
		{
			if (store == null) { Out.WriteLine("  No active configuration."); return; }
			if (!Confirm("  Rebuild the manifest from the on-disk record tree?")) { Out.WriteLine("  Cancelled."); return; }
			Out.WriteLine("  Rebuilding manifest from the record tree …");
			store.RebuildManifestFromTree((n, total) => ReportProgress("records", n, total));
			Out.WriteLine();
			Out.WriteLine($"  Manifest rebuilt. Messages: {store.Manifest.message_count:N0}, next pointer: {store.Manifest.next_pointer:N0}.");
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Wipes and rebuilds the search index from the record tree. </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		///
		/// <param name="store">	The store. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static void RebuildIndex(Archive_Store store)
		{
			if (store == null) { Out.WriteLine("  No active configuration."); return; }
			if (!Confirm("  Wipe and rebuild the search index from records? (can take a while)")) { Out.WriteLine("  Cancelled."); return; }
			Out.WriteLine("  Rebuilding search index from the record tree …");
			long n = store.RebuildIndexFromTree((done, total) => ReportProgress("records", done, total));
			Out.WriteLine();
			Out.WriteLine($"  Reindexed {n:N0} record(s). Index documents: {store.Index.DocumentCount():N0}.");
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	In-place progress line for the long rebuild operations. </summary>
		///
		/// <remarks>	I Volt, 7/3/2026. </remarks>
		///
		/// <param name="noun"> 	What is being processed (e.g. "records"). </param>
		/// <param name="done"> 	Items processed so far. </param>
		/// <param name="total">	Estimated total (0 = unknown). </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static void ReportProgress(string noun, long done, long total)
		{
			if (total > 0)
			{
				double pct = 100.0 * done / total;
				if (pct > 100) pct = 100;
				Out.Write($"\r    … {done:N0} / {total:N0} {noun} ({pct,3:F0}%)     ");
			}
			else
			{
				Out.Write($"\r    … {done:N0} {noun}     ");
			}
		}

		// =====================================================================
		// ITEM 1 — Edit configuration file
		// =====================================================================

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Loads a DPAPI-protected configuration, lets the operator edit common fields one at a time,
		/// and re-saves it protected. Secrets are shown masked. If no email is supplied, prompts for one.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="email">	(Optional) The email. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static void EditConfiguration(string email = null)
		{
			if (string.IsNullOrWhiteSpace(email))
			{
				Out.Write("  Email of the configuration to edit: ");
				email = (Console.ReadLine() ?? string.Empty).Trim();
				if (string.IsNullOrWhiteSpace(email)) { Out.WriteLine("  Cancelled."); return; }
			}

			string path = Gmail_IMAP_Management_Class.PathFor(email);
			if (!File.Exists(path))
			{
				Out.WriteLine($"  No configuration found at: {path}");
				return;
			}

			Configuration_Definition_Container cfg;
			try
			{
				cfg = Configuration_Definition_Container.FromJson(
					Gmail_IMAP_Management_Class.UnprotectFile(path));
			}
			catch (Exception ex)
			{
				Out.WriteLine($"  Could not open/decrypt configuration: {ex.Message}");
				return;
			}

			bool dirty = false;
			while (true)
			{
				Out.WriteLine();
				Out.WriteLine($"  ===== EDIT: {cfg.email} =====");
				Out.WriteLine($"   1. Email                         : {cfg.email}");
				Out.WriteLine($"   2. Password (secret)             : {Mask(cfg.password)}");
				Out.WriteLine($"   3. App password (secret)         : {Mask(cfg.server?.application_specific_password)}");
				Out.WriteLine($"   4. Archive folder                : {cfg.archive_folder}");
				Out.WriteLine($"   5. Archive index folder          : {cfg.archive_index_folder}");
				Out.WriteLine($"   6. Attachments folder            : {cfg.archive_attachments_folder}");
				Out.WriteLine($"   7. Archive structure             : {cfg.archive_structure}");
				Out.WriteLine($"   8. Max emails/minute             : {cfg.performance?.max_emails_per_minute}");
				Out.WriteLine($"   9. Max attachment MB/minute      : {cfg.performance?.max_attachment_size_in_MB_per_minute}");
				Out.WriteLine($"  10. Pause between requests (s)    : {cfg.performance?.pause_between_requests_in_seconds}");
				Out.WriteLine($"  11. Max retries                  : {cfg.performance?.max_retries}");
				Out.WriteLine($"  12. Log level                    : {cfg.performance?.log_level}");
				Out.WriteLine($"  13. Archive attachments (on/off) : {cfg.archive_attachments}");
				Out.WriteLine("   S. Save    Q. Quit without saving");
				Out.Write("  Choose: ");
				string c = (Console.ReadLine() ?? string.Empty).Trim().ToUpperInvariant();

				if (c == "Q")
				{
					if (dirty && !Confirm("  Discard unsaved changes?")) continue;
					Out.WriteLine("  Edit cancelled."); return;
				}
				if (c == "S")
				{
					try
					{
						Gmail_IMAP_Management_Class.ProtectToFile(cfg.ToJson(), path);
						Out.WriteLine($"  Saved (DPAPI-protected): {path}");
					}
					catch (Exception ex) { Out.WriteLine($"  Save failed: {ex.Message}"); }
					return;
				}

				cfg.server ??= new Server();
				cfg.performance ??= new Performance();

				switch (c)
				{
					case "1": cfg.email = Edit("Email", cfg.email); dirty = true; break;
					case "2": { var s = EditSecret("Password"); if (s != null) { cfg.password = s; dirty = true; } break; }
					case "3":
						{
							var s = EditSecret("App password");
							if (s != null)
							{
								cfg.server.application_specific_password = s;
								// keep SMTP secrets in step with the app password
								cfg.server.smtp_password = s;
								cfg.server.smtp_application_specific_password = s;
								dirty = true;
							}
							break;
						}
					case "4": cfg.archive_folder = Edit("Archive folder", cfg.archive_folder); dirty = true; break;
					case "5": cfg.archive_index_folder = Edit("Index folder", cfg.archive_index_folder); dirty = true; break;
					case "6": cfg.archive_attachments_folder = Edit("Attachments folder", cfg.archive_attachments_folder); dirty = true; break;
					case "7": cfg.archive_structure = Edit("Archive structure", cfg.archive_structure); dirty = true; break;
					case "8": cfg.performance.max_emails_per_minute = EditLong("Max emails/minute", cfg.performance.max_emails_per_minute); dirty = true; break;
					case "9": cfg.performance.max_attachment_size_in_MB_per_minute = EditLong("Max attachment MB/minute", cfg.performance.max_attachment_size_in_MB_per_minute); dirty = true; break;
					case "10": cfg.performance.pause_between_requests_in_seconds = EditLong("Pause (s)", cfg.performance.pause_between_requests_in_seconds); dirty = true; break;
					case "11": cfg.performance.max_retries = EditLong("Max retries", cfg.performance.max_retries); dirty = true; break;
					case "12": cfg.performance.log_level = Edit("Log level", cfg.performance.log_level); dirty = true; break;
					case "13":
						cfg.archive_attachments = !(cfg.archive_attachments ?? true);
						Out.WriteLine($"  Archive attachments -> {cfg.archive_attachments}"); dirty = true; break;
					default: Out.WriteLine("  Unrecognized option."); break;
				}
			}
		}

		// =====================================================================
		// ITEM 2 — Retry errors found in the logs
		// =====================================================================

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Reads the per-account failure log, reconnects to Gmail, and re-fetches + re-processes each
		/// unresolved UID using the same defensive parse and store path. Successful UIDs are marked
		/// resolved so repeated runs converge. Returns true if all attempted UIDs now succeed.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="cfg">  	The configuration. </param>
		/// <param name="store">	The store. </param>
		///
		/// <returns>	True if it succeeds, false if it fails. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static bool RetryLoggedErrors(Configuration_Definition_Container cfg, Archive_Store store)
		{
			if (cfg == null || store == null) { Out.WriteLine("  No active configuration."); return false; }

			var log = new Failure_Log(store.ArchiveFolder, store.AccountKey);
			var pending = log.UnresolvedDistinct();
			if (pending.Count == 0)
			{
				Out.WriteLine("  No unresolved errors in the log. Nothing to retry.");
				return true;
			}

			Out.WriteLine($"  {pending.Count} unresolved error(s) in the log:");
			foreach (var e in pending.Take(20))
				Out.WriteLine($"    UID {e.uid}  [{e.folder}]  {Trunc(e.reason, 60)}");
			if (pending.Count > 20) Out.WriteLine($"    … and {pending.Count - 20} more.");
			if (!Confirm("  Retry these now?")) { Out.WriteLine("  Cancelled."); return false; }

			var engine = new Gmail_Fetch_Engine(cfg, store, Out);
			var resolved = new List<uint>();
			int ok = 0, still = 0, done = 0, total = pending.Count;

			// Group by folder so we open each mailbox once.
			foreach (var group in pending.GroupBy(e => string.IsNullOrEmpty(e.folder) ? "[Gmail]/All Mail" : e.folder))
			{
				ImapClient client = null;
				IMailFolder folder = null;
				try
				{
					client = engine.Connect();
					folder = client.GetFolder(group.Key);
					folder.Open(FolderAccess.ReadOnly);

					foreach (var entry in group)
					{
						var uid = new UniqueId(folder.UidValidity, entry.uid);
						try
						{
							bool success = engine.RetryOne(folder, uid);
							if (success) { resolved.Add(entry.uid); ok++; }
							else still++;
						}
						catch (Exception ex)
						{
							still++;
							Out.WriteLine($"    UID {entry.uid} still failing: {ex.Message}");
						}
						done++;
						Out.Write($"\r    … retried {done}/{total}  (recovered {ok}, still failing {still})     ");
					}
				}
				catch (Exception ex)
				{
					Out.WriteLine($"  Could not process folder '{group.Key}': {ex.Message}");
				}
				finally
				{
					try { folder?.Close(); } catch { }
					try { client?.Disconnect(true); } catch { }
					client?.Dispose();
				}
			}

			Out.WriteLine();
			if (resolved.Count > 0)
			{
				store.Index.Commit();
				store.SaveManifest();
				log.MarkResolved(resolved);
			}

			Out.WriteLine($"  Retry complete. Recovered: {ok}. Still failing: {still}.");
			return still == 0;
		}

		// ---- small console helpers ---------------------------------------

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Edits. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="label">  	The label. </param>
		/// <param name="current">	The current. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string Edit(string label, string current)
		{
			Out.Write($"  {label} [{current}]: ");
			string v = (Console.ReadLine() ?? string.Empty).Trim();
			return string.IsNullOrEmpty(v) ? current : v;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Edit long. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="label">  	The label. </param>
		/// <param name="current">	The current. </param>
		///
		/// <returns>	A long? </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static long? EditLong(string label, long? current)
		{
			Out.Write($"  {label} [{current}]: ");
			string v = (Console.ReadLine() ?? string.Empty).Trim();
			if (string.IsNullOrEmpty(v)) return current;
			return long.TryParse(v, out long n) ? n : current;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Edit secret. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="label">	The label. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string EditSecret(string label)
		{
			Out.Write($"  {label} (masked, Enter to keep): ");
			var sb = new System.Text.StringBuilder();
			ConsoleKeyInfo k;
			while ((k = Console.ReadKey(true)).Key != ConsoleKey.Enter)
			{
				if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) { sb.Length--; Out.Write("\b \b"); } }
				else if (!char.IsControl(k.KeyChar)) { sb.Append(k.KeyChar); Out.Write('*'); }
			}
			Out.WriteLine();
			return sb.Length == 0 ? null : sb.ToString();
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Confirms. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="prompt">	The prompt. </param>
		///
		/// <returns>	True if it succeeds, false if it fails. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static bool Confirm(string prompt)
		{
			Out.Write($"{prompt} (y/N): ");
			string v = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
			return v == "y" || v == "yes";
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Masks. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="s">	The string. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string Mask(string s) =>
			string.IsNullOrEmpty(s) ? "(unset)" : new string('•', Math.Min(s.Length, 12));

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Truncs. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="s">	The string. </param>
		/// <param name="n">	An int to process. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string Trunc(string s, int n) =>
			string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");
	}
}