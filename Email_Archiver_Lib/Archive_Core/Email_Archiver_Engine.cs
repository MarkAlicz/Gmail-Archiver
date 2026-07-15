
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MailKit.Net.Smtp;

using IVolt.Core.Email.Configuration;
using IVolt.Core.Email.Gmail.Plugins;

namespace IVolt.Core.Email.Gmail
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Top-level interactive engine. Launched by Email_Archiver.exe. Presents the startup choice
    /// (continue/open vs. new configuration), verifies the configuration, then drives the main menu:
    /// test connections, show summary, continue processing, search the archive, run plugins, switch
    /// configuration. Designed to hold the operator's hand, especially through connection failures.
    /// </summary>
    ///
    /// <remarks>	I Volt, 6/30/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public static class Email_Archiver_Engine
    {
        /// <summary>	(Immutable) the out. </summary>
        private static readonly TextWriter Out = Console.Out;

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Starts this object. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static void Start()
        {
            Banner();
            Configuration_Definition_Container cfg = StartupChoice();
            if (cfg == null) { Out.WriteLine("No configuration loaded. Exiting."); return; }

            using var store = new Archive_Store(cfg);
            MainMenu(cfg, store);
        }

        // ---- Startup ------------------------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Startup choice. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <returns>	A Configuration_Definition_Container. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static Configuration_Definition_Container StartupChoice()
        {
            while (true)
            {
                Out.WriteLine();
                Out.WriteLine("  [1] Continue — open an existing configuration");
                Out.WriteLine("  [2] New configuration");
                Out.WriteLine("  [0] Exit");
                Out.Write("  Choose: ");
                switch ((Console.ReadLine() ?? "").Trim())
                {
                    case "1":
                        // Open an EXISTING configuration (returns null with guidance if none).
                        try { var c = Gmail_IMAP_Management_Class.RunOpenExisting(); if (c != null) return c; break; }
                        catch (Exception ex) { Out.WriteLine($"  ! Could not open configuration: {ex.Message}"); break; }
                    case "2":
                        // Force-create a NEW configuration (prompts before overwriting an existing one).
                        try { var c = Gmail_IMAP_Management_Class.RunCreateNew(); if (c != null) return c; break; }
                        catch (Exception ex) { Out.WriteLine($"  ! Could not create configuration: {ex.Message}"); break; }
                    case "0":
                        return null;
                    default:
                        Out.WriteLine("  Please choose 1, 2, or 0.");
                        break;
                }
            }
        }

        // ---- Main menu ----------------------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Main menu. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="cfg">  	The configuration. </param>
        /// <param name="store">	The store. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static void MainMenu(Configuration_Definition_Container cfg, Archive_Store store)
        {
            Out.WriteLine();
            Out.WriteLine($"  Configuration verified for: {cfg.email}");
            while (true)
            {
                Out.WriteLine();
                Out.WriteLine("  ===== MAIN MENU =====");
                Out.WriteLine("  1. Test Connections");
                Out.WriteLine("  2. Show Log / Data Summary");
                Out.WriteLine("  3. Continue Processing Configuration File");
                Out.WriteLine("  4. Search the Archive (email + attachments)");
                Out.WriteLine("  5. Run Plugin");
                Out.WriteLine("  6. Switch Configuration");
				Out.WriteLine("  7. Operations SubSystem");
				Out.WriteLine("  0. Exit");
                Out.Write("  Choose: ");
                switch ((Console.ReadLine() ?? "").Trim())
                {
                    case "1": TestConnections(cfg); break;
                    case "2": ShowSummary(store); break;
                    case "3": ContinueProcessing(cfg, store); break;
                    case "4": Search_Console.Run(store); break;
                    case "5": RunPluginMenu(cfg, store); break;
                    case "6":
                        var next = StartupChoice();
                        if (next != null) { MainMenu(next, new Archive_Store(next)); return; }
                        break;
					case "7": Archive_Operations.BuildMenu(cfg, store).Show(); break;  // "Operations"
					case "0": return;
                    default: Out.WriteLine("  Unrecognized option."); break;
                }
            }
        }

        // ---- 1. Test connections (hand-holding diagnostics) --------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Tests connections. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="cfg">	The configuration. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static void TestConnections(Configuration_Definition_Container cfg)
        {
            Out.WriteLine();
            Out.WriteLine("  Testing IMAP …");
            TestImap(cfg);
            Out.WriteLine("  Testing SMTP …");
            TestSmtp(cfg);
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Tests IMAP. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="cfg">	The configuration. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static void TestImap(Configuration_Definition_Container cfg)
        {
            string host = cfg.server?.imap ?? "imap.gmail.com";
            int port = (int)(cfg.server?.port ?? 993);
            try
            {
                using var client = new ImapClient();
                client.Connect(host, port, SecureSocketOptions.SslOnConnect);
                client.Authenticate(cfg.email, cfg.server?.application_specific_password ?? cfg.password);
                var inbox = client.GetFolder("[Gmail]/All Mail");
                inbox.Open(FolderAccess.ReadOnly);
                Out.WriteLine($"    ✓ IMAP OK. '[Gmail]/All Mail' holds {inbox.Count} messages.");
                client.Disconnect(true);
            }
            catch (AuthenticationException)
            {
                Out.WriteLine("    ✗ IMAP authentication failed.");
                Out.WriteLine("      Most likely cause: the app-specific password is wrong or was revoked,");
                Out.WriteLine("      or 2-Step Verification isn't enabled on the Google account.");
                Out.WriteLine("      Fix: Google Account → Security → App passwords → generate a new 16-char");
                Out.WriteLine("      password, then re-run 'New configuration' to update it.");
            }
            catch (SslHandshakeException ex)
            {
                Out.WriteLine("    ✗ TLS handshake failed: " + ex.Message);
                Out.WriteLine("      Fix: check the system clock and that a proxy/AV isn't intercepting TLS.");
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Out.WriteLine("    ✗ Could not reach the server: " + ex.Message);
                Out.WriteLine($"      Fix: verify network access to {host}:{port} and that a firewall isn't blocking it.");
            }
            catch (Exception ex)
            {
                Out.WriteLine("    ✗ IMAP failed: " + ex.Message);
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Tests SMTP. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="cfg">	The configuration. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static void TestSmtp(Configuration_Definition_Container cfg)
        {
            string host = cfg.server?.smtp ?? "smtp.gmail.com";
            int port = (int)(cfg.server?.smtp_port ?? 587);
            try
            {
                using var client = new SmtpClient();
                client.Connect(host, port, SecureSocketOptions.StartTls);
                if (cfg.server?.smtp_authentication ?? true)
                    client.Authenticate(cfg.server?.smtp_username ?? cfg.email,
                        cfg.server?.smtp_application_specific_password ?? cfg.server?.smtp_password ?? cfg.password);
                Out.WriteLine("    ✓ SMTP OK (authenticated).");
                client.Disconnect(true);
            }
            catch (AuthenticationException)
            {
                Out.WriteLine("    ✗ SMTP authentication failed — same app-password guidance as IMAP above.");
            }
            catch (Exception ex)
            {
                Out.WriteLine("    ✗ SMTP failed: " + ex.Message);
            }
        }

        // ---- 2. Summary ---------------------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Shows the summary. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="store">	The store. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static void ShowSummary(Archive_Store store)
        {
            var m = store.Manifest;
            Out.WriteLine();
            Out.WriteLine("  ===== ARCHIVE SUMMARY =====");
            Out.WriteLine($"    Account:      {m.email}");
            Out.WriteLine($"    Messages:     {m.message_count}");
            Out.WriteLine($"    Attachments:  {m.attachment_count}");
            Out.WriteLine($"    Date range:   {Fmt(m.earliest_date)}  →  {Fmt(m.latest_date)}");
            Out.WriteLine($"    Index docs:   {store.Index.DocumentCount()}");
            Out.WriteLine($"    Next pointer: {m.next_pointer}");
            Out.WriteLine($"    Last run:     {Fmt(m.last_run_utc)}");
            if (m.journal_recovered_on_load > 0)
                Out.WriteLine($"    Recovered:    {m.journal_recovered_on_load} record(s) replayed from the journal on load.");
            if (m.message_count == 0)
                Out.WriteLine("    (Nothing archived yet — choose 'Continue Processing' to begin.)");

            ShowContinuation(store);
        }

        // ---- 2b. Continuation / recovery analysis -------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Renders the recovery / continuation report: cross-checks manifest vs. on-disk records vs.
        /// the search index, flags drift and unresolved failures, and prints a recommended next action.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="store">	The store. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static void ShowContinuation(Archive_Store store)
        {
            Out.WriteLine();
            Out.Write("  Analyzing archive (scanning records) …");
            var r = store.AnalyzeContinuation(n => Out.Write($"\r  Analyzing archive … {n:N0} records scanned     "));
            Out.WriteLine();
            Out.WriteLine("  ===== RECOVERY / CONTINUATION =====");
            Out.WriteLine($"    Manifest messages : {r.ManifestMessageCount:N0}");
            Out.WriteLine($"    Records on disk   : {r.DiskRecordCount:N0}   (drift {r.ManifestVsDiskDrift:+#;-#;0})");
            Out.WriteLine($"    Unique Gmail ids  : {r.UniqueMessageIds:N0}" +
                          (r.DuplicateRecords > 0 ? $"   ({r.DuplicateRecords:N0} duplicate record file(s))" : ""));
            Out.WriteLine($"    Index documents   : {r.IndexDocCount:N0} / {r.ExpectedIndexDocs:N0} expected   (drift {r.IndexDrift:+#;-#;0})");
            Out.WriteLine($"    Pending journal   : {r.PendingJournalEntries} entr(y/ies) awaiting fold");
            Out.WriteLine($"    Unresolved errors : {r.UnresolvedFailures}");
            Out.WriteLine($"    Resume position   : UID > {r.HighWaterUid} (UIDVALIDITY {r.UidValidity}), next pointer {r.NextPointer:N0}");
            Out.WriteLine($"    Consistency       : {(r.IsConsistent ? "OK — all sources agree" : "DRIFT DETECTED")}");
            Out.WriteLine($"    Recommendation    : {r.Recommendation()}");
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Fmts. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="iso">	The ISO. </param>
        ///
        /// <returns>	The formatted value. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static string Fmt(string iso) =>
            DateTimeOffset.TryParse(iso, out var d) ? d.ToString("yyyy-MM-dd") : "—";

        // ---- 3. Continue processing (scope selector + fetch loop) --------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Continue processing. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="cfg">  	The configuration. </param>
        /// <param name="store">	The store. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static void ContinueProcessing(Configuration_Definition_Container cfg, Archive_Store store)
        {
            var opts = new Fetch_Options();
            Out.WriteLine();
            Out.WriteLine("  Processing scope:");
            Out.WriteLine("    1. Full extract (everything in All Mail)");
            Out.WriteLine("    2. Resume (skip already-archived) [recommended]");
            Out.WriteLine("    3. Date range");
            Out.WriteLine("    4. New since last run (UID high-water)");
            Out.WriteLine("    5. Specific label / folder");
            Out.WriteLine("    0. Back");
            Out.Write("  Choose: ");
            switch ((Console.ReadLine() ?? "").Trim())
            {
                case "1": opts.Scope = Fetch_Scope.FullExtract; break;
                case "2": opts.Scope = Fetch_Scope.Resume; break;
                case "3":
                    opts.Scope = Fetch_Scope.DateRange;
                    opts.Since = AskDate("  Since (yyyy-MM-dd, blank = none): ");
                    opts.Before = AskDate("  Before (yyyy-MM-dd, blank = none): ");
                    break;
                case "4": opts.Scope = Fetch_Scope.UidRange; break;
                case "5":
                    opts.Scope = Fetch_Scope.Label;
                    Out.Write("  Label/folder [[Gmail]/All Mail]: ");
                    var lbl = (Console.ReadLine() ?? "").Trim();
                    if (!string.IsNullOrEmpty(lbl)) opts.Label = lbl;
                    break;
                default: return;
            }

            // Ctrl+C requests a graceful stop (finish queued work + checkpoint) instead of killing
            // the process. A second Ctrl+C lets the runtime terminate normally.
            using var cts = new System.Threading.CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (s, e) =>
            {
                if (!cts.IsCancellationRequested)
                {
                    e.Cancel = true; // keep running; stop pulling new mail and wind down cleanly
                    Out.WriteLine();
                    Out.WriteLine("  ⏹ Stopping after the current messages … (press Ctrl+C again to force-quit)");
                    cts.Cancel();
                }
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                var engine = new Gmail_Fetch_Engine(cfg, store, Out);
                long n = engine.Run(opts, cts.Token);
                Out.WriteLine(cts.IsCancellationRequested
                    ? $"  Stopped. {n} new message(s) archived before cancelling; resume any time."
                    : $"  Done. {n} new message(s) archived.");
            }
            catch (AuthenticationException)
            {
                Out.WriteLine("  ✗ Authentication failed. Run 'Test Connections' for guided help.");
            }
            catch (Exception ex)
            {
                Out.WriteLine("  ✗ Processing stopped: " + ex.Message);
                Out.WriteLine("    Progress up to the last checkpoint was saved; you can resume safely.");
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Ask date. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="prompt">	The prompt. </param>
        ///
        /// <returns>	A DateTime? </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static DateTime? AskDate(string prompt)
        {
            Out.Write(prompt);
            var s = (Console.ReadLine() ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return null;
            return DateTime.TryParse(s, out var d) ? d : (DateTime?)null;
        }

        // ---- 4. Search  →  Search_Console.Run (main menu option 4) --------

        // ---- 5. Plugins ---------------------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Executes the 'plugin menu' operation. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="cfg">  	The configuration. </param>
        /// <param name="store">	The store. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static void RunPluginMenu(Configuration_Definition_Container cfg, Archive_Store store)
        {
            Out.WriteLine();
            Out.WriteLine("  Scanning for plugins …");
            var plugins = Plugin_Loader.LoadAll(Out);
            Out.WriteLine($"  Found {plugins.Count} plugin(s).");
            if (plugins.Count == 0)
            {
                Out.WriteLine("  No plugins found. Drop I_Run_Code_Against_Configuration implementations");
                Out.WriteLine("  into the 'Plugins' folder beside the executable, then try again.");
                return;
            }
            Out.WriteLine();
            Out.WriteLine("  ===== PLUGINS =====");
            for (int i = 0; i < plugins.Count; i++)
                Out.WriteLine($"    {i + 1}. {plugins[i].Plugin_Name} — {plugins[i].Plugin_Description}");
            Out.WriteLine("    0. Back");
            Out.Write("  Choose: ");
            if (int.TryParse((Console.ReadLine() ?? "").Trim(), out int sel) && sel >= 1 && sel <= plugins.Count)
            {
                var plugin = plugins[sel - 1];
                Out.WriteLine($"  Running '{plugin.Plugin_Name}' …");
                try
                {
                    bool ok = plugin.Run(cfg, store, Out);
                    Out.WriteLine(ok ? "  ✓ Plugin finished." : "  ! Plugin reported a failure.");
                }
                catch (Exception ex)
                {
                    Out.WriteLine("  ✗ Plugin threw: " + ex.Message);
                }
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Banners this object. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static void Banner()
        {
            Out.WriteLine("============================================================");
            Out.WriteLine("  IVolt Gmail IMAP Archiver");
            Out.WriteLine("  Privacy-First Technology & Security · ivolt.io");
            Out.WriteLine("============================================================");
        }
    }
}
