
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace IVolt.Core.Email.Gmail
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Interactive, unified search console over one account's Lucene index. A single query box searches
    /// email content AND attachments together (senders, subject, body, headers, attachment names, and
    /// extracted attachment text), with Lucene field syntax plus friendly aliases. Results are
    /// paginated; opening a result shows the full record (recipients, body snippet, attachment list
    /// with sizes and an "indexed" flag, and the on-disk attachment ZIP path).
    /// </summary>
    ///
    /// <remarks>	I Volt, 7/3/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public static class Search_Console
    {
        private const int PageSize = 15;
        private const int MaxResults = 500;
        private static readonly TextWriter Out = Console.Out;

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Runs the search console until the operator chooses to go back. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="store">	The store. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static void Run(Archive_Store store)
        {
            if (store == null) { Out.WriteLine("  No active configuration."); return; }

            int docs = store.Index.DocumentCount();
            if (docs == 0)
            {
                Out.WriteLine();
                Out.WriteLine("  The search index is empty. Archive some mail first (Continue Processing),");
                Out.WriteLine("  or rebuild it via Operations -> 'Rebuild Search Index From Tree'.");
                return;
            }

            Header(docs);
            PrintHelp();

            var results = new List<Archive_Index.Search_Hit>();
            string queryText = null;
            int page = 0;

            while (true)
            {
                Out.Write("\nsearch> ");
                string input = (Console.ReadLine() ?? string.Empty).Trim();
                if (input.Length == 0) continue;

                string lower = input.ToLowerInvariant();
                if (lower is "q" or ":q" or "quit" or "exit" or "back") return;
                if (lower is "?" or "help" or ":help") { PrintHelp(); continue; }

                // Pagination / open only make sense with an active result set.
                if (results.Count > 0)
                {
                    if (lower is "n" or "next") { page = ClampPage(page + 1, results.Count); ShowPage(store, results, queryText, page); continue; }
                    if (lower is "p" or "prev" or "previous") { page = ClampPage(page - 1, results.Count); ShowPage(store, results, queryText, page); continue; }
                    if (int.TryParse(input, out int sel) && sel >= 1 && sel <= results.Count)
                    {
                        if (OpenDetail(store, results[sel - 1])) return; // 'q' from detail quits search
                        ShowPage(store, results, queryText, page);
                        continue;
                    }
                }

                // Otherwise treat the line as a new query.
                queryText = input;
                Out.Write("  Searching …");
                try
                {
                    results = store.Index.QueryHits(ApplyAliases(queryText), null, MaxResults);
                }
                catch (Exception ex)
                {
                    Out.WriteLine($"\r  ! Bad query: {ex.Message}     ");
                    results = new List<Archive_Index.Search_Hit>();
                    continue;
                }
                Out.Write("\r             \r");   // clear the "Searching …" line
                page = 0;
                ShowPage(store, results, queryText, page);
            }
        }

        // ---- Rendering ----------------------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Prints one page of results. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="store">  	The store. </param>
        /// <param name="results">	The results. </param>
        /// <param name="query">  	The query text (for the header). </param>
        /// <param name="page">   	The zero-based page. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static void ShowPage(Archive_Store store, List<Archive_Index.Search_Hit> results, string query, int page)
        {
            Out.WriteLine();
            if (results.Count == 0)
            {
                Out.WriteLine($"  No matches for \"{query}\". Try broader terms, wildcards (inv*), or a field (from:alice).");
                return;
            }

            int pages = (results.Count + PageSize - 1) / PageSize;
            int start = page * PageSize;
            int end = Math.Min(start + PageSize, results.Count);

            string shown = results.Count >= MaxResults ? $"{MaxResults}+ " : results.Count.ToString();
            WriteColor(ConsoleColor.Cyan, $"  {shown} match(es) for \"{query}\"  —  page {page + 1}/{pages}");
            Out.WriteLine("  ─────────────────────────────────────────────────────────────────────");

            for (int i = start; i < end; i++)
            {
                var h = results[i];
                string date = FmtDateKey(h.DateKey);
                string from = Pad(Trunc(Clean(h.From), 26), 26);
                string subj = Trunc(Clean(h.Subject), 42);
                string att = string.IsNullOrWhiteSpace(h.AttachmentNames) ? "" : "  \U0001F4CE"; // paperclip
                Out.WriteLine($"  {i + 1,4}. {date}  {from}  {subj}{att}");
            }

            Out.WriteLine("  ─────────────────────────────────────────────────────────────────────");
            Out.WriteLine("  Enter a number to open · n/p page · type a new query · ? help · q back");
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Shows the full record for a hit. Returns true if the operator asked to quit. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="store">	The store. </param>
        /// <param name="hit">  	The selected hit. </param>
        ///
        /// <returns>	True to quit the search console. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static bool OpenDetail(Archive_Store store, Archive_Index.Search_Hit hit)
        {
            var rec = store.LoadRecord(hit.Pointer);
            if (rec == null)
            {
                Out.WriteLine($"  Record #{hit.Pointer} could not be loaded (moved or deleted).");
                return false;
            }

            Out.WriteLine();
            Out.WriteLine("  ══════════════════════════════════════════════════════════════════════");
            WriteColor(ConsoleColor.Cyan, $"   #{rec.archive_pointer}    score {hit.Score:F2}");
            Out.WriteLine($"   Date:    {FmtIso(rec.date)}");
            Out.WriteLine($"   From:    {Clean(rec.from)}");
            if (rec.to != null && rec.to.Count > 0) Out.WriteLine($"   To:      {Trunc(string.Join(", ", rec.to), 66)}");
            if (rec.cc != null && rec.cc.Count > 0) Out.WriteLine($"   Cc:      {Trunc(string.Join(", ", rec.cc), 66)}");
            Out.WriteLine($"   Subject: {Clean(rec.subject)}");
            if (rec.gm_labels != null && rec.gm_labels.Count > 0)
                Out.WriteLine($"   Labels:  {string.Join(", ", rec.gm_labels)}");

            Out.WriteLine("   ── Body ──");
            PrintSnippet(rec.text_body, 800);

            if (rec.attachments != null && rec.attachments.Count > 0)
            {
                Out.WriteLine($"   ── Attachments ({rec.attachments.Count}) ──");
                for (int i = 0; i < rec.attachments.Count; i++)
                {
                    var a = rec.attachments[i];
                    string flag = a.content_indexed ? "[indexed]" : "";
                    Out.WriteLine($"    {i + 1}. {Pad(Trunc(a.file_name, 34), 34)}  {Pad(Trunc(a.content_type, 24), 24)}  {Pad(HumanSize(a.size_bytes), 9)} {flag}");
                }
                Out.WriteLine($"   ZIP: {store.AttachmentZipPath(rec.archive_pointer)}");
            }
            Out.WriteLine("  ══════════════════════════════════════════════════════════════════════");

            while (true)
            {
                Out.Write("   [Enter=back · f=full body · o=show ZIP path · q=quit search] ");
                string c = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
                if (c.Length == 0 || c == "b" || c == "back") return false;
                if (c == "q" || c == "quit") return true;
                if (c == "f") { Out.WriteLine(); Out.WriteLine(rec.text_body ?? "(no text body)"); continue; }
                if (c == "o")
                {
                    if (rec.attachments != null && rec.attachments.Count > 0)
                        Out.WriteLine("   " + store.AttachmentZipPath(rec.archive_pointer));
                    else
                        Out.WriteLine("   (no attachments)");
                    continue;
                }
                Out.WriteLine("   Unrecognized — Enter to go back, or f / o / q.");
            }
        }

        // ---- Query aliases ------------------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Rewrites friendly field prefixes to the internal index field names so users don't have to
        /// know them: att:/attachment: -&gt; att_names, content:/body-of-attachment -&gt; att_content,
        /// header: -&gt; headers. Other prefixes (from:/to:/cc:/bcc:/subject:/body:/date:) already match.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="query">	The user query. </param>
        ///
        /// <returns>	The rewritten query. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static string ApplyAliases(string query)
        {
            query = Regex.Replace(query, @"(?i)\battachment:", "att_names:");
            query = Regex.Replace(query, @"(?i)\batt:", "att_names:");
            query = Regex.Replace(query, @"(?i)\bcontent:", "att_content:");
            query = Regex.Replace(query, @"(?i)\bheader:", "headers:");
            return query;
        }

        // ---- Header / help ------------------------------------------------

        private static void Header(int docs)
        {
            Out.WriteLine();
            WriteColor(ConsoleColor.Cyan, "  ══════════════════ SEARCH ARCHIVE ══════════════════");
            Out.WriteLine($"   {docs:N0} document(s) indexed — email and attachments together.");
        }

        private static void PrintHelp()
        {
            Out.WriteLine();
            Out.WriteLine("   Just type words to search everything (senders, subjects, bodies,");
            Out.WriteLine("   headers, attachment names AND attachment text).");
            Out.WriteLine();
            Out.WriteLine("   Scope with a field prefix:");
            Out.WriteLine("     from:alice     to:bob        subject:\"tax return\"");
            Out.WriteLine("     body:meeting   header:list-id date:20240101");
            Out.WriteLine("     att:invoice.pdf   (attachment name)");
            Out.WriteLine("     content:refund    (text inside attachments)");
            Out.WriteLine("   Operators: AND OR NOT, quotes for phrases, * wildcard (inv*), ~ fuzzy.");
            Out.WriteLine("   e.g.  from:bank content:statement AND date:2024*");
            Out.WriteLine();
            Out.WriteLine("   In results: <number> open · n/p page · new query anytime · q back");
        }

        // ---- Small helpers ------------------------------------------------

        private static int ClampPage(int page, int count)
        {
            int pages = Math.Max(1, (count + PageSize - 1) / PageSize);
            if (page < 0) return 0;
            if (page > pages - 1) return pages - 1;
            return page;
        }

        private static void PrintSnippet(string body, int max)
        {
            if (string.IsNullOrWhiteSpace(body)) { Out.WriteLine("   (no text body)"); return; }
            string s = Regex.Replace(body, @"\s+", " ").Trim();
            if (s.Length > max) s = s.Substring(0, max) + " …";
            // wrap to ~72 cols, indented
            const int width = 72;
            for (int i = 0; i < s.Length; i += width)
                Out.WriteLine("   " + s.Substring(i, Math.Min(width, s.Length - i)));
        }

        private static string FmtDateKey(string yyyymmdd)
        {
            if (!string.IsNullOrEmpty(yyyymmdd) && yyyymmdd.Length == 8)
                return $"{yyyymmdd.Substring(0, 4)}-{yyyymmdd.Substring(4, 2)}-{yyyymmdd.Substring(6, 2)}";
            return "----------";
        }

        private static string FmtIso(string iso) =>
            DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
                ? d.ToString("yyyy-MM-dd HH:mm") : (iso ?? "—");

        private static string HumanSize(long bytes)
        {
            if (bytes < 0) bytes = 0;
            string[] u = { "B", "KB", "MB", "GB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return i == 0 ? $"{(long)v} {u[i]}" : $"{v:F1} {u[i]}";
        }

        private static string Clean(string s) =>
            string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, @"\s+", " ").Trim();

        private static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, Math.Max(0, n - 1)) + "…");

        private static string Pad(string s, int n) =>
            (s ?? "").Length >= n ? s : (s ?? "").PadRight(n);

        private static void WriteColor(ConsoleColor color, string text)
        {
            try
            {
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Out.WriteLine(text);
                Console.ForegroundColor = prev;
            }
            catch { Out.WriteLine(text); }
        }
    }
}
