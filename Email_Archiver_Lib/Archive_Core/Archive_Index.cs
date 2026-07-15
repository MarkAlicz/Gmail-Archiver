
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using LuceneDirectory = Lucene.Net.Store.Directory;

namespace IVolt.Core.Email.Gmail
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Thin Lucene.NET wrapper for the local archive. One index per account under INDEX\{sanitized}\
    /// . Fields: pointer, gm_msgid, from, to, cc, bcc, subject, headers, body, att_names,
    /// att_content, date. Writing is incremental (one document per message, added during archival).
    /// Searching is field-scoped to back the search menu (senders / attachment names / attachment
    /// content / email text / headers).
    /// </summary>
    ///
    /// <remarks>	I Volt, 6/30/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public sealed class Archive_Index : IDisposable
    {
        /// <summary>	(Immutable) the version. </summary>
        private const LuceneVersion Version = LuceneVersion.LUCENE_48;

        /// <summary>	(Immutable) the pointer. </summary>
        public const string F_POINTER      = "pointer";
        /// <summary>	(Immutable) the gm msgid. </summary>
        public const string F_GM_MSGID     = "gm_msgid";
        /// <summary>	(Immutable) source for the. </summary>
        public const string F_FROM         = "from";
        /// <summary>	(Immutable) to. </summary>
        public const string F_TO           = "to";
        /// <summary>	(Immutable) the Cc. </summary>
        public const string F_CC           = "cc";
        /// <summary>	(Immutable) the Bcc. </summary>
        public const string F_BCC          = "bcc";
        /// <summary>	(Immutable) the subject. </summary>
        public const string F_SUBJECT      = "subject";
        /// <summary>	(Immutable) the headers. </summary>
        public const string F_HEADERS      = "headers";
        /// <summary>	(Immutable) the body. </summary>
        public const string F_BODY         = "body";
        /// <summary>	(Immutable) list of names of the atts. </summary>
        public const string F_ATT_NAMES    = "att_names";
        /// <summary>	(Immutable) the att content. </summary>
        public const string F_ATT_CONTENT  = "att_content";
        /// <summary>	(Immutable) yyyyMMdd for range queries. </summary>
        public const string F_DATE         = "date";

        /// <summary>	(Immutable) full pathname of the index file. </summary>
        private readonly string _indexPath;
        /// <summary>	(Immutable) the analyzer. </summary>
        private readonly StandardAnalyzer _analyzer;
        /// <summary>	The dir. </summary>
        private LuceneDirectory _dir;
        /// <summary>	The writer. </summary>
        private IndexWriter _writer;
        /// <summary>	Guards lazy writer construction under concurrent AddOrUpdate. </summary>
        private readonly object _writeInit = new object();

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Constructor. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="indexFolder">	Pathname of the index folder. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public Archive_Index(string indexFolder)
        {
            _indexPath = indexFolder;
            System.IO.Directory.CreateDirectory(_indexPath);
            _analyzer = new StandardAnalyzer(Version);
            _dir = FSDirectory.Open(_indexPath);
        }

        // ---- Writing ------------------------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Opens for write. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void OpenForWrite()
        {
            if (_writer != null) return;
            // Guard the lazy init: the parallel fetch pipeline can call AddOrUpdate from many
            // threads at once. Once constructed, IndexWriter's own methods are thread-safe.
            lock (_writeInit)
            {
                if (_writer != null) return;
                var cfg = new IndexWriterConfig(Version, _analyzer)
                {
                    OpenMode = OpenMode.CREATE_OR_APPEND
                };
                _writer = new IndexWriter(_dir, cfg);
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Adds (or replaces, keyed by gm_msgid) one message document. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="rec">					The record. </param>
        /// <param name="attachmentContent">	The attachment content. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void AddOrUpdate(Archive_Record rec, string attachmentContent)
        {
            OpenForWrite();
            var doc = new Document
            {
                new StringField(F_POINTER, rec.archive_pointer.ToString(), Field.Store.YES),
                new StringField(F_GM_MSGID, rec.gm_msgid ?? string.Empty, Field.Store.YES),
                new TextField(F_FROM, rec.from ?? string.Empty, Field.Store.YES),
                new TextField(F_TO, string.Join(" ", rec.to ?? new List<string>()), Field.Store.NO),
                new TextField(F_CC, string.Join(" ", rec.cc ?? new List<string>()), Field.Store.NO),
                new TextField(F_BCC, string.Join(" ", rec.bcc ?? new List<string>()), Field.Store.NO),
                new TextField(F_SUBJECT, rec.subject ?? string.Empty, Field.Store.YES),
                new TextField(F_HEADERS, FlattenHeaders(rec), Field.Store.NO),
                new TextField(F_BODY, rec.text_body ?? string.Empty, Field.Store.NO),
                new TextField(F_ATT_NAMES,
                    string.Join(" ", (rec.attachments ?? new List<Archive_Attachment>()).Select(a => a.file_name)),
                    Field.Store.YES),
                new TextField(F_ATT_CONTENT, attachmentContent ?? string.Empty, Field.Store.NO),
                new StringField(F_DATE, ToDateKey(rec.date), Field.Store.YES),
            };

            // Replace any existing doc for this gm_msgid so re-runs are idempotent.
            if (!string.IsNullOrEmpty(rec.gm_msgid))
                _writer.UpdateDocument(new Term(F_GM_MSGID, rec.gm_msgid), doc);
            else
                _writer.AddDocument(doc);
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Commits this object. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void Commit() => _writer?.Commit();

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Removes every document, for a from-scratch index rebuild. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void DeleteAll()
        {
            OpenForWrite();
            _writer.DeleteAll();
            _writer.Commit();
        }

        // ---- Searching ----------------------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Field-scoped search. Returns matching archive pointers, most relevant first.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="field">	 	The field. </param>
        /// <param name="queryText"> 	The query text. </param>
        /// <param name="maxResults">	(Optional) The maximum results. </param>
        ///
        /// <returns>	A List&lt;long&gt; </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public List<long> Search(string field, string queryText, int maxResults = 200)
        {
            _writer?.Commit();
            using var reader = DirectoryReader.Open(_dir);
            var searcher = new IndexSearcher(reader);
            var parser = new QueryParser(Version, field, _analyzer);
            parser.AllowLeadingWildcard = true;

            Query query;
            try { query = parser.Parse(queryText); }
            catch (ParseException) { query = new TermQuery(new Term(field, queryText.ToLowerInvariant())); }

            var hits = searcher.Search(query, maxResults).ScoreDocs;
            var results = new List<long>(hits.Length);
            foreach (var h in hits)
            {
                var doc = searcher.Doc(h.Doc);
                if (long.TryParse(doc.Get(F_POINTER), out long ptr))
                    results.Add(ptr);
            }
            return results;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Every free-text field, used for "search everything" (email + attachments). </summary>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static readonly string[] AllTextFields =
        {
            F_FROM, F_TO, F_CC, F_BCC, F_SUBJECT, F_HEADERS, F_BODY, F_ATT_NAMES, F_ATT_CONTENT
        };

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	One search result with the fields needed to render a result line. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public sealed class Search_Hit
        {
            /// <summary>	Archive pointer (load the full record with it). </summary>
            public long Pointer { get; set; }
            /// <summary>	Relevance score. </summary>
            public float Score { get; set; }
            /// <summary>	Stored From header. </summary>
            public string From { get; set; }
            /// <summary>	Stored Subject. </summary>
            public string Subject { get; set; }
            /// <summary>	Stored space-joined attachment names. </summary>
            public string AttachmentNames { get; set; }
            /// <summary>	Stored yyyyMMdd date key. </summary>
            public string DateKey { get; set; }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Rich search. When <paramref name="field"/> is null/empty the query runs across every text
        /// field (email bodies, headers, senders AND attachment names + extracted attachment content);
        /// otherwise it is scoped to that one field. Lucene query syntax is supported (e.g.
        /// <c>from:alice subject:"tax return" content:refund</c>). Returns hits, most relevant first,
        /// carrying stored fields so the caller need not load records to render the list.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="queryText"> 	The query text. </param>
        /// <param name="field">	 	(Optional) A single field to scope to, or null for all fields. </param>
        /// <param name="maxResults">	(Optional) The maximum results. </param>
        ///
        /// <returns>	The hits. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public List<Search_Hit> QueryHits(string queryText, string field = null, int maxResults = 200)
        {
            var results = new List<Search_Hit>();
            if (string.IsNullOrWhiteSpace(queryText)) return results;

            _writer?.Commit();
            DirectoryReader reader;
            try { reader = DirectoryReader.Open(_dir); }
            catch { return results; } // no index yet
            using (reader)
            {
                var searcher = new IndexSearcher(reader);
                Query query = BuildQuery(queryText, field);
                var hits = searcher.Search(query, Math.Max(1, maxResults)).ScoreDocs;
                foreach (var h in hits)
                {
                    var doc = searcher.Doc(h.Doc);
                    long.TryParse(doc.Get(F_POINTER), out long ptr);
                    results.Add(new Search_Hit
                    {
                        Pointer = ptr,
                        Score = h.Score,
                        From = doc.Get(F_FROM),
                        Subject = doc.Get(F_SUBJECT),
                        AttachmentNames = doc.Get(F_ATT_NAMES),
                        DateKey = doc.Get(F_DATE),
                    });
                }
            }
            return results;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Builds a parsed query, tolerating bad syntax by falling back to a term query. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="queryText">	The query text. </param>
        /// <param name="field">	 	The scoped field, or null for all. </param>
        ///
        /// <returns>	A Query. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private Query BuildQuery(string queryText, string field)
        {
            try
            {
                if (string.IsNullOrEmpty(field))
                {
                    var mp = new MultiFieldQueryParser(Version, AllTextFields, _analyzer)
                    {
                        AllowLeadingWildcard = true
                    };
                    return mp.Parse(queryText);
                }
                var parser = new QueryParser(Version, field, _analyzer) { AllowLeadingWildcard = true };
                return parser.Parse(queryText);
            }
            catch (ParseException)
            {
                // Treat the raw text as a single term in the chosen (or body) field.
                return new TermQuery(new Term(field ?? F_BODY, queryText.ToLowerInvariant()));
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Document count. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <returns>	An int. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public int DocumentCount()
        {
            try
            {
                _writer?.Commit();   // flush any pending writes so the count is never stale
                using var reader = DirectoryReader.Open(_dir);
                return reader.NumDocs;
            }
            catch { return 0; }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Flatten headers. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="rec">	The record. </param>
        ///
        /// <returns>	A string. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static string FlattenHeaders(Archive_Record rec)
        {
            if (rec.headers == null) return string.Empty;
            return string.Join(" ", rec.headers.Select(h => h.Key + " " + h.Value));
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Converts an ISO to a date key. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="iso">	The ISO. </param>
        ///
        /// <returns>	ISO as a string. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static string ToDateKey(string iso)
        {
            if (DateTimeOffset.TryParse(iso, out var d)) return d.ToString("yyyyMMdd");
            return string.Empty;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged
        /// resources.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void Dispose()
        {
            _writer?.Dispose();
            _dir?.Dispose();
            _analyzer?.Dispose();
        }
    }
}
