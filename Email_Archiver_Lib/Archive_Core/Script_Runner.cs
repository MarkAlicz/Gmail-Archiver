
using System;
using System.Globalization;
using System.IO;
using System.Threading;

using MailKit.Security;

using IVolt.Core.Email.Configuration;

namespace IVolt.Core.Email.Gmail
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Non-interactive script runner for headless / scheduled archiving (Task Scheduler, CI, cron via
    /// a wrapper). Invoked with <c>Gmail_Archiver -s "path\to\script.ias"</c>. The script is a plain
    /// text file, one command per line; blank lines and lines starting with '#' are ignored. The
    /// configuration must already exist (created once interactively) because DPAPI decryption is
    /// silent and no prompts are shown.
    ///
    /// Commands (case-insensitive):
    ///   EMAIL &lt;address&gt;                     load + decrypt that account's configuration
    ///   SCOPE FULL | RESUME | NEW               set the processing scope
    ///   SCOPE DATERANGE &lt;since&gt; &lt;before&gt;      yyyy-MM-dd bounds (either may be '-')
    ///   SCOPE LABEL &lt;folder&gt;                   e.g. "[Gmail]/All Mail"
    ///   PROCESS                                 run the archival pipeline with the current scope
    ///   VERIFY [DEEP]                           file verification + confidence verdict
    ///   REBUILD MANIFEST | INDEX               reconcile from the record tree
    ///   SUMMARY                                 print the recovery / continuation report
    ///
    /// Returns a process exit code: 0 on success, non-zero if any command failed.
    /// </summary>
    ///
    /// <remarks>	I Volt, 7/3/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public static class Script_Runner
    {
        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Executes a script file. Returns a process exit code (0 = success). </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="scriptPath">	Full path to the .ias script. </param>
        /// <param name="output">	 	Output writer (defaults to Console.Out). </param>
        ///
        /// <returns>	0 on success; 1 if any command failed or the script could not be read. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static int Run(string scriptPath, TextWriter output = null)
        {
            var Out = output ?? Console.Out;

            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            {
                Out.WriteLine($"Script not found: {scriptPath}");
                return 1;
            }

            string[] lines;
            try { lines = File.ReadAllLines(scriptPath); }
            catch (Exception ex) { Out.WriteLine($"Could not read script: {ex.Message}"); return 1; }

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (s, e) =>
            {
                if (!cts.IsCancellationRequested)
                {
                    e.Cancel = true;
                    Out.WriteLine();
                    Out.WriteLine("  ⏹ Cancelling after current work … (Ctrl+C again to force-quit)");
                    cts.Cancel();
                }
            };
            Console.CancelKeyPress += onCancel;

            Archive_Store store = null;
            Configuration_Definition_Container cfg = null;
            var opts = new Fetch_Options();
            int exit = 0;

            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    if (cts.IsCancellationRequested) { Out.WriteLine("  Script cancelled."); exit = 1; break; }

                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    string[] tok = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    string cmd = tok[0].ToUpperInvariant();
                    Out.WriteLine($"› {line}");

                    try
                    {
                        switch (cmd)
                        {
                            case "EMAIL":
                                store?.Dispose();
                                store = null;
                                if (tok.Length < 2) { Fail(Out, ref exit, "EMAIL requires an address."); break; }
                                cfg = Gmail_IMAP_Management_Class.Load(tok[1]);
                                var problems = cfg.Validate();
                                if (problems.Count > 0)
                                {
                                    foreach (var p in problems) Out.WriteLine($"    ! {p}");
                                    Fail(Out, ref exit, $"Configuration for '{tok[1]}' is invalid.");
                                    cfg = null;
                                    break;
                                }
                                store = new Archive_Store(cfg);
                                opts = new Fetch_Options();
                                Out.WriteLine($"    Loaded {cfg.email} ({store.Manifest.message_count} archived).");
                                break;

                            case "SCOPE":
                                if (!SetScope(tok, opts, Out)) Fail(Out, ref exit, "Bad SCOPE.");
                                break;

                            case "PROCESS":
                                if (!Require(store, Out, ref exit)) break;
                                var engine = new Gmail_Fetch_Engine(cfg, store, Out);
                                long n = engine.Run(opts, cts.Token);
                                Out.WriteLine($"    {n} new message(s) archived.");
                                break;

                            case "VERIFY":
                                if (!Require(store, Out, ref exit)) break;
                                bool deep = tok.Length > 1 && tok[1].Equals("DEEP", StringComparison.OrdinalIgnoreCase);
                                var report = store.VerifyFiles(deep);
                                report.Render(Out);
                                if (!report.CanContinue) Fail(Out, ref exit, "Verification: NOT SAFE to continue.");
                                break;

                            case "REBUILD":
                                if (!Require(store, Out, ref exit)) break;
                                string what = tok.Length > 1 ? tok[1].ToUpperInvariant() : "";
                                if (what == "MANIFEST") { store.RebuildManifestFromTree(); Out.WriteLine("    Manifest rebuilt."); }
                                else if (what == "INDEX") { long r = store.RebuildIndexFromTree(); Out.WriteLine($"    Reindexed {r} record(s)."); }
                                else Fail(Out, ref exit, "REBUILD expects MANIFEST or INDEX.");
                                break;

                            case "SUMMARY":
                                if (!Require(store, Out, ref exit)) break;
                                Email_Archiver_Engine.ShowContinuation(store);
                                break;

                            default:
                                Fail(Out, ref exit, $"Unknown command '{cmd}'.");
                                break;
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        Fail(Out, ref exit, $"No configuration found (line {i + 1}). Create it interactively first.");
                    }
                    catch (AuthenticationException)
                    {
                        Fail(Out, ref exit, $"Authentication failed (line {i + 1}). Check the app password.");
                    }
                    catch (Exception ex)
                    {
                        Fail(Out, ref exit, $"Line {i + 1} failed: {ex.Message}");
                    }
                }
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
                store?.Dispose();
            }

            Out.WriteLine(exit == 0 ? "Script completed successfully." : "Script completed with errors.");
            return exit;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Applies a SCOPE command to the options. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="tok"> 	The tokenized line. </param>
        /// <param name="opts">	The options to mutate. </param>
        /// <param name="Out"> 	The output. </param>
        ///
        /// <returns>	True if the scope was recognized. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static bool SetScope(string[] tok, Fetch_Options opts, TextWriter Out)
        {
            if (tok.Length < 2) return false;
            switch (tok[1].ToUpperInvariant())
            {
                case "FULL": opts.Scope = Fetch_Scope.FullExtract; return true;
                case "RESUME": opts.Scope = Fetch_Scope.Resume; return true;
                case "NEW": opts.Scope = Fetch_Scope.UidRange; return true;
                case "LABEL":
                    if (tok.Length < 3) return false;
                    opts.Scope = Fetch_Scope.Label;
                    opts.Label = string.Join(' ', tok, 2, tok.Length - 2).Trim('"');
                    return true;
                case "DATERANGE":
                    opts.Scope = Fetch_Scope.DateRange;
                    opts.Since = ParseDate(tok.Length > 2 ? tok[2] : "-");
                    opts.Before = ParseDate(tok.Length > 3 ? tok[3] : "-");
                    return true;
                default: return false;
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Parses a yyyy-MM-dd date, or null for '-' / unparseable. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="s">	The token. </param>
        ///
        /// <returns>	The date, or null. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static DateTime? ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s == "-") return null;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
                ? d : (DateTime?)null;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Guards commands that need a loaded account. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="store">	The store (may be null). </param>
        /// <param name="Out">  	The output. </param>
        /// <param name="exit"> 	[in,out] The exit code to raise on failure. </param>
        ///
        /// <returns>	True if a store is loaded. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static bool Require(Archive_Store store, TextWriter Out, ref int exit)
        {
            if (store != null) return true;
            Fail(Out, ref exit, "No account loaded — issue an EMAIL command first.");
            return false;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Records a failure and raises the exit code. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="Out">   	The output. </param>
        /// <param name="exit">  	[in,out] The exit code. </param>
        /// <param name="reason">	The reason. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static void Fail(TextWriter Out, ref int exit, string reason)
        {
            Out.WriteLine($"    ✗ {reason}");
            exit = 1;
        }
    }
}
