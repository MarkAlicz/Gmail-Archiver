
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using IVolt.Core.Email.Configuration;

namespace IVolt.Core.Email.Gmail
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Owns the on-disk local archive for one account: BYMONTH record writing, per-message
    /// attachment ZIPs, the Lucene index, and the crash-safe manifest. Implements IArchiveContext
    /// for search/plugins. All paths derive from the loaded configuration.
    ///
    /// Concurrency: StoreMessage is thread-safe and is called concurrently by the parallel fetch
    /// pipeline. Heavy per-message work (ZIP, text extraction, record write, Lucene add) runs
    /// lock-free; only pointer assignment, manifest bookkeeping and the durable journal append are
    /// serialized under a single lock. The Lucene IndexWriter is itself thread-safe.
    ///
    /// Durability: every stored message is journaled (append-only, flushed). Full manifest snapshots
    /// happen only at checkpoints, which fold + truncate the journal. A run killed between
    /// checkpoints loses nothing — the next load replays the journal delta exactly.
    /// </summary>
    ///
    /// <remarks>	I Volt, 6/30/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public sealed class Archive_Store : IArchiveContext, IDisposable
    {
        /// <summary>	(Immutable) the configuration. </summary>
        private readonly Configuration_Definition_Container _cfg;
        /// <summary>	(Immutable) full pathname of the manifest file. </summary>
        private readonly string _manifestPath;
        /// <summary>	(Immutable) append-only recovery journal. </summary>
        private readonly Archive_Journal _journal;
        /// <summary>	Serializes pointer assignment, manifest mutation and journal append. </summary>
        private readonly object _sync = new object();
        /// <summary>	BYMONTH directories already created this session (skips a CreateDirectory syscall per message). </summary>
        private readonly ConcurrentDictionary<string, bool> _dirsCreated = new ConcurrentDictionary<string, bool>();

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Gets the account key. </summary>
        ///
        /// <value>	The account key. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public string AccountKey { get; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Gets the pathname of the archive folder. </summary>
        ///
        /// <value>	The pathname of the archive folder. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public string ArchiveFolder { get; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Gets the pathname of the attachments folder. </summary>
        ///
        /// <value>	The pathname of the attachments folder. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public string AttachmentsFolder { get; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Gets the pathname of the index folder. </summary>
        ///
        /// <value>	The pathname of the index folder. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public string IndexFolder { get; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Gets or sets the manifest. </summary>
        ///
        /// <value>	The manifest. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public Archive_Manifest Manifest { get; private set; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Gets the zero-based index of this object. </summary>
        ///
        /// <value>	The index. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public Archive_Index Index { get; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Constructor. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <exception cref="ArgumentNullException">		Thrown when one or more required arguments
        /// 												are null. </exception>
        /// <exception cref="InvalidOperationException">	Thrown when the requested operation is
        /// 												invalid. </exception>
        ///
        /// <param name="cfg">	The configuration. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public Archive_Store(Configuration_Definition_Container cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            AccountKey = Gmail_IMAP_Management_Class.SanitizeEmail(cfg.email);

            ArchiveFolder     = cfg.archive_folder             ?? throw new InvalidOperationException("archive_folder not set.");
            AttachmentsFolder = cfg.archive_attachments_folder ?? Path.Combine(ArchiveFolder, "ATTACHMENTS");
            IndexFolder       = cfg.archive_index_folder       ?? throw new InvalidOperationException("archive_index_folder not set.");

            System.IO.Directory.CreateDirectory(ArchiveFolder);
            System.IO.Directory.CreateDirectory(AttachmentsFolder);
            System.IO.Directory.CreateDirectory(IndexFolder);

            _manifestPath = Path.Combine(ArchiveFolder, AccountKey + ".manifest.json");
            _journal = new Archive_Journal(ArchiveFolder, AccountKey);
            Manifest = LoadOrCreateManifest();
            Index = new Archive_Index(IndexFolder);
        }

        // ---- Manifest -----------------------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Loads the last snapshot (or a fresh manifest) and replays the append-only journal delta on
        /// top of it, so state is crash-exact even if the previous run was killed between checkpoints.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <returns>	The recovered manifest. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private Archive_Manifest LoadOrCreateManifest()
        {
            Archive_Manifest man = null;
            if (File.Exists(_manifestPath))
            {
                try { man = Archive_Manifest.FromJson(File.ReadAllText(_manifestPath)); }
                catch { man = null; }
            }
            man ??= new Archive_Manifest
            {
                account_key = AccountKey,
                email = _cfg.email,
                next_pointer = _cfg.archive_pointer_starting_increment ?? 1212,
            };

            // Guard against nulls from older / hand-edited snapshots.
            man.seen ??= new Dictionary<string, long>();
            man.record_paths ??= new Dictionary<long, string>();

            // Replay journal delta (idempotent: pointers already folded are skipped).
            long recovered = 0;
            foreach (var e in _journal.ReadAll())
            {
                if (man.record_paths.ContainsKey(e.pointer)) continue;
                ApplyJournalEntry(man, e);
                recovered++;
            }
            man.journal_recovered_on_load = recovered;
            return man;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Folds one journal entry into a manifest during recovery. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="man">	The manifest being reconstructed. </param>
        /// <param name="e">  	The journal entry. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static void ApplyJournalEntry(Archive_Manifest man, Archive_Journal.Entry e)
        {
            if (!string.IsNullOrEmpty(e.gm_msgid)) man.seen[e.gm_msgid] = e.pointer;
            if (!string.IsNullOrEmpty(e.record_path)) man.record_paths[e.pointer] = e.record_path;
            man.message_count += 1;
            man.attachment_count += e.attachments;
            if (e.pointer >= man.next_pointer) man.next_pointer = e.pointer + 1;
            UpdateRange(man, e.date);
            if (e.uid_validity != 0 &&
                (man.uid_validity == 0 || man.uid_validity == e.uid_validity) &&
                e.uid > man.high_water_uid)
            {
                man.high_water_uid = e.uid;
                man.uid_validity = e.uid_validity;
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Atomic full-snapshot write (temp + replace) so a crash never corrupts it. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void SaveManifest()
        {
            lock (_sync) SaveManifestLocked();
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Snapshot writer; caller must hold <see cref="_sync"/>. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private void SaveManifestLocked()
        {
            Manifest.last_run_utc = DateTimeOffset.UtcNow.ToString("o");
            string tmp = _manifestPath + ".tmp";
            File.WriteAllText(tmp, Manifest.ToJson());
            if (File.Exists(_manifestPath)) File.Replace(tmp, _manifestPath, null);
            else File.Move(tmp, _manifestPath);
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Durable checkpoint: commit the index, write a full manifest snapshot, then fold + truncate
        /// the journal. Cheap enough to call periodically; the journal makes the window between
        /// checkpoints lossless, so checkpoints exist for compaction, not correctness.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void Checkpoint()
        {
            lock (_sync)
            {
                Index.Commit();
                SaveManifestLocked();   // in-memory manifest already reflects every journaled message
                _journal.Truncate();    // its content is now in the snapshot
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Already archived. Thread-safe. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="gmMsgId">	Identifier for the gm message. </param>
        ///
        /// <returns>	True if it succeeds, false if it fails. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public bool AlreadyArchived(string gmMsgId)
        {
            if (string.IsNullOrEmpty(gmMsgId)) return false;
            lock (_sync) return Manifest.seen.ContainsKey(gmMsgId);
        }

        // ---- Storing a message -------------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Persists one parsed message: writes the BYMONTH JSON record, zips attachments (honoring
        /// config), indexes enabled fields, journals + updates the manifest. Thread-safe. Returns the
        /// assigned archive pointer.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="msg">	The message. </param>
        ///
        /// <returns>	A long. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public long StoreMessage(Gmail_IMAP_Message_Container msg)
        {
            // Reserve a pointer atomically; everything keyed off it is then collision-free.
            long pointer;
            lock (_sync) { pointer = Manifest.next_pointer; Manifest.next_pointer = pointer + 1; }

            // BYMONTH bucket from message date (fallback: internal date, then 'unknown').
            var (year, month) = BucketFor(msg);
            string relDir = Path.Combine(year, month);
            string absDir = Path.Combine(ArchiveFolder, relDir);
            // CreateDirectory is idempotent + thread-safe; cache the result so we don't hit the
            // filesystem once per message. (The dict is only populated after a successful create,
            // so "key present ⇒ directory exists" always holds.)
            if (!_dirsCreated.ContainsKey(absDir))
            {
                System.IO.Directory.CreateDirectory(absDir);
                _dirsCreated.TryAdd(absDir, true);
            }

            var rec = BuildRecord(msg, pointer);
            rec.record_path = Path.Combine(relDir, pointer + ".json");

            // Attachments -> per-message ZIP (if enabled), collecting extracted text for the index.
            // Heavy: runs lock-free (unique paths per pointer).
            string attachmentText = null;
            int attCount = 0;
            if ((_cfg.archive_attachments ?? true) && msg.Parts != null)
            {
                var attParts = msg.Parts.Where(p => p.IsAttachment ||
                                    !string.IsNullOrEmpty(p.FileName)).ToList();
                if (attParts.Count > 0)
                {
                    string zipAbs = AttachmentZipPath(pointer);
                    // Correct relative path from the archive root to the actual ZIP, wherever the
                    // configured attachments folder lives (no hardcoded "..\ATTACHMENTS" assumption).
                    string zipRel = Path.GetRelativePath(ArchiveFolder, zipAbs);
                    attachmentText = WriteAttachmentZip(zipAbs, attParts, rec);
                    rec.attachment_zip = zipRel;
                    attCount = attParts.Count;
                }
            }

            // Write the record JSON (unique path) and index it (Lucene writer is thread-safe).
            File.WriteAllText(Path.Combine(ArchiveFolder, rec.record_path), rec.ToJson());
            IndexRecord(rec, attachmentText);

            // Commit shared bookkeeping + durable journal line atomically. The journal append lives
            // inside the lock so a concurrent Checkpoint() sees a manifest that matches the journal
            // exactly (no truncate/append race).
            lock (_sync)
            {
                if (!string.IsNullOrEmpty(rec.gm_msgid))
                    Manifest.seen[rec.gm_msgid] = pointer;
                Manifest.record_paths[pointer] = rec.record_path;
                Manifest.message_count += 1;
                Manifest.attachment_count += attCount;
                UpdateDateRange(rec.date);
                if (msg.UidValidity != 0 &&
                    (Manifest.uid_validity == 0 || Manifest.uid_validity == msg.UidValidity) &&
                    msg.Uid > Manifest.high_water_uid)
                {
                    Manifest.high_water_uid = msg.Uid;
                    Manifest.uid_validity = msg.UidValidity;
                }

                _journal.Append(new Archive_Journal.Entry
                {
                    pointer = pointer,
                    gm_msgid = rec.gm_msgid,
                    record_path = rec.record_path,
                    attachments = attCount,
                    date = rec.date,
                    uid = msg.Uid,
                    uid_validity = msg.UidValidity
                });
            }

            return pointer;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Index record. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="rec">			 	The record. </param>
        /// <param name="attachmentText">	The attachment text. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private void IndexRecord(Archive_Record rec, string attachmentText)
        {
            // Clone-with-blanks per configuration index switches, then index.
            var indexed = new Archive_Record
            {
                archive_pointer = rec.archive_pointer,
                gm_msgid = rec.gm_msgid,
                date = rec.date,
                subject   = (_cfg.index_email_subject ?? true) ? rec.subject : null,
                from      = (_cfg.index_email_from ?? true) ? rec.from : null,
                to        = (_cfg.index_email_to ?? true) ? rec.to : new List<string>(),
                cc        = (_cfg.index_email_cc ?? true) ? rec.cc : new List<string>(),
                bcc       = (_cfg.index_email_bcc ?? true) ? rec.bcc : new List<string>(),
                text_body = (_cfg.index_email_body ?? true) ? rec.text_body : null,
                headers   = rec.headers,
                attachments = (_cfg.index_email_attachments ?? true) ? rec.attachments : new List<Archive_Attachment>(),
            };
            string attText = (_cfg.index_email_attachments ?? true) ? attachmentText : null;
            Index.AddOrUpdate(indexed, attText);
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Bucket for. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="msg">	The message. </param>
        ///
        /// <returns>	A Tuple. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private (string year, string month) BucketFor(Gmail_IMAP_Message_Container msg)
        {
            DateTimeOffset? d = msg.Date ?? msg.InternalDate;
            if (d.HasValue) return (d.Value.Year.ToString("D4"), d.Value.Month.ToString("D2"));
            return ("unknown", "unknown");
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Attachment zip path. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="archivePointer">	The archive pointer. </param>
        ///
        /// <returns>	A string. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public string AttachmentZipPath(long archivePointer) =>
            Path.Combine(AttachmentsFolder, archivePointer + ".zip");

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Writes an attachment zip. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="zipPath">	Full pathname of the zip file. </param>
        /// <param name="parts">  	The parts. </param>
        /// <param name="rec">	  	The record. </param>
        ///
        /// <returns>	A string. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private string WriteAttachmentZip(string zipPath, List<GmailMimePart> parts, Archive_Record rec)
        {
            var sb = new StringBuilder();
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                int n = 0;
                foreach (var p in parts)
                {
                    n++;
                    string safeName = MakeSafeName(p.FileName, n, p.ContentType);
                    var entry = zip.CreateEntry(safeName, CompressionLevel.Optimal);
                    using (var es = entry.Open())
                        es.Write(p.RawContent, 0, p.RawContent.Length);

                    string extracted = Attachment_Text_Extractor.Extract(safeName, p.ContentType, p.RawContent);
                    bool indexed = !string.IsNullOrEmpty(extracted);
                    if (indexed) sb.Append(extracted).Append('\n');

                    rec.attachments.Add(new Archive_Attachment
                    {
                        file_name = p.FileName,
                        content_type = p.ContentType,
                        content_id = p.ContentId,
                        size_bytes = p.RawContent?.LongLength ?? 0,
                        zip_entry = safeName,
                        content_indexed = indexed
                    });
                }
            }
            return sb.ToString();
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Makes safe name. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="name">		  	The name. </param>
        /// <param name="ordinal">	  	The ordinal. </param>
        /// <param name="contentType">	Type of the content. </param>
        ///
        /// <returns>	A string. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static string MakeSafeName(string name, int ordinal, string contentType)
        {
            if (string.IsNullOrWhiteSpace(name))
                name = "attachment_" + ordinal + GuessExt(contentType);
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return ordinal.ToString("D3") + "_" + name;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Guess extent. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="ct">	The ct. </param>
        ///
        /// <returns>	A string. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static string GuessExt(string ct)
        {
            ct = (ct ?? "").ToLowerInvariant();
            if (ct.Contains("pdf")) return ".pdf";
            if (ct.Contains("png")) return ".png";
            if (ct.Contains("jpeg") || ct.Contains("jpg")) return ".jpg";
            if (ct.StartsWith("text/")) return ".txt";
            return ".bin";
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Updates the range described by ISO. Caller holds <see cref="_sync"/>. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="iso">	The ISO. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private void UpdateDateRange(string iso) => UpdateRange(Manifest, iso);

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Builds a record. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="m">	  	A Gmail_IMAP_Message_Container to process. </param>
        /// <param name="pointer">	The pointer. </param>
        ///
        /// <returns>	An Archive_Record. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private Archive_Record BuildRecord(Gmail_IMAP_Message_Container m, long pointer)
        {
            return new Archive_Record
            {
                archive_pointer = pointer,
                gm_msgid = m.GmMsgId != 0 ? m.GmMsgId.ToString() : null,
                gm_thrid = m.GmThrId != 0 ? m.GmThrId.ToString() : null,
                gm_labels = m.GmLabels != null ? new List<string>(m.GmLabels) : new List<string>(),
                uid = m.Uid,
                raw_sha256 = m.RawSha256,
                message_id = m.MessageId,
                date = m.Date?.ToString("o"),
                internal_date = m.InternalDate?.ToString("o"),
                subject = m.Subject,
                from = m.From?.ToString(),
                to = m.To?.Select(a => a.ToString()).ToList() ?? new List<string>(),
                cc = m.Cc?.Select(a => a.ToString()).ToList() ?? new List<string>(),
                bcc = m.Bcc?.Select(a => a.ToString()).ToList() ?? new List<string>(),
                headers = m.Headers ?? new List<KeyValuePair<string, string>>(),
                text_body = m.TextBody,
                html_body = m.HtmlBody,
            };
        }

        // ---- IArchiveContext read surface --------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Enumerates the records in this collection. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <returns>
        /// An enumerator that allows foreach to be used to process the records in this collection.
        /// </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public IEnumerable<Archive_Record> EnumerateRecords()
        {
            if (!System.IO.Directory.Exists(ArchiveFolder)) yield break;
            foreach (var file in System.IO.Directory.EnumerateFiles(ArchiveFolder, "*.json", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.EndsWith(".verification.json", StringComparison.OrdinalIgnoreCase)) continue;
                Archive_Record rec = null;
                try { rec = Archive_Record.FromJson(File.ReadAllText(file)); } catch { }
                if (rec != null) yield return rec;
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Loads a single record by pointer in O(1) via the manifest pointer index, with a self-healing
        /// O(n) tree-scan fallback that repairs a missing/stale index entry.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="pointer">	The pointer. </param>
        ///
        /// <returns>	The record, or null if not found. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public Archive_Record LoadRecord(long pointer)
        {
            string rel;
            lock (_sync) Manifest.record_paths.TryGetValue(pointer, out rel);

            if (!string.IsNullOrEmpty(rel))
            {
                string abs = Path.Combine(ArchiveFolder, rel);
                if (File.Exists(abs))
                {
                    try { return Archive_Record.FromJson(File.ReadAllText(abs)); } catch { /* fall through */ }
                }
            }

            // Fallback: scan once, and remember the path so we never pay for this pointer again.
            foreach (var rec in EnumerateRecords())
            {
                if (rec.archive_pointer != pointer) continue;
                if (!string.IsNullOrEmpty(rec.record_path))
                    lock (_sync) Manifest.record_paths[pointer] = rec.record_path;
                return rec;
            }
            return null;
        }

        // ---- Recovery / continuation -------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Cross-checks the three sources of truth (manifest, on-disk records, Lucene index) plus the
        /// failure log, and reports drift and a recommended next action. This is the "where am I, and
        /// is anything inconsistent" view the operator gets before continuing a large job.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="onProgress">	Optional callback with the running record-scan count. </param>
        ///
        /// <returns>	A populated continuation report. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public Continuation_Report AnalyzeContinuation(Action<long> onProgress = null)
        {
            var r = new Continuation_Report
            {
                ManifestMessageCount    = Manifest.message_count,
                ManifestAttachmentCount = Manifest.attachment_count,
                NextPointer             = Manifest.next_pointer,
                HighWaterUid            = Manifest.high_water_uid,
                UidValidity             = Manifest.uid_validity,
                EarliestDate            = Manifest.earliest_date,
                LatestDate              = Manifest.latest_date,
                JournalRecoveredOnLoad  = Manifest.journal_recovered_on_load,
                PendingJournalEntries   = _journal.Count(),
                IndexDocCount           = Index.DocumentCount(),
            };

            // Count PARSEABLE records — the same definition RebuildManifestFromTree uses for
            // message_count — so a rebuild actually reconciles the drift (a raw file count would
            // include stray/partial .json files and could never converge). Also track distinct Gmail
            // ids and id-less records so the index (which dedups by Gmail id) is compared against the
            // right baseline, and duplicate record files are reported honestly. Progress as we go.
            long disk = 0, noId = 0;
            var gmids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rec in EnumerateRecords())
            {
                disk++;
                if (string.IsNullOrEmpty(rec.gm_msgid)) noId++;
                else gmids.Add(rec.gm_msgid);
                if (onProgress != null && disk % 500 == 0) onProgress(disk);
            }
            onProgress?.Invoke(disk);
            r.DiskRecordCount = disk;
            r.UniqueMessageIds = gmids.Count;
            r.RecordsWithoutId = noId;

            var log = new Failure_Log(ArchiveFolder, AccountKey);
            r.UnresolvedFailures = log.UnresolvedDistinct().Count;
            return r;
        }

        // ---- File-level verification -------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Verifies the physical archive files and resume metadata, returning a confidence verdict on
        /// whether downloading can safely continue from where it left off. Quick mode checks presence +
        /// integrity (manifest/journal/index openable, every indexed record file exists, orphan scan);
        /// deep mode additionally opens every record JSON and every referenced attachment ZIP. The
        /// result is persisted as {account}.verification.json so each run leaves a dated stamp.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="deep">	    	True to open every record + attachment ZIP (slower, thorough). </param>
        /// <param name="onProgress">	Optional (checked, total) callback for a progress indicator. </param>
        ///
        /// <returns>	A populated verification report. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public Verification_Report VerifyFiles(bool deep, Action<long, long> onProgress = null)
        {
            var r = new Verification_Report
            {
                AccountKey = AccountKey,
                ArchiveFolder = ArchiveFolder,
                Deep = deep,
                VerifiedUtc = DateTimeOffset.UtcNow.ToString("o"),
            };

            // ---- Integrity ----
            r.ManifestFileExists = File.Exists(_manifestPath);
            try
            {
                if (r.ManifestFileExists) Archive_Manifest.FromJson(File.ReadAllText(_manifestPath));
                r.ManifestReadable = true;   // in-memory manifest is authoritative even without a snapshot
            }
            catch { r.ManifestReadable = false; }

            try { r.PendingJournalEntries = _journal.Count(); r.JournalReadable = true; }
            catch { r.JournalReadable = false; }

            try { r.IndexDocCount = Index.DocumentCount(); r.IndexOpenable = true; }
            catch { r.IndexOpenable = false; }

            // ---- Resume position ----
            r.ManifestMessageCount = Manifest.message_count;
            r.NextPointer = Manifest.next_pointer;
            r.HighWaterUid = Manifest.high_water_uid;
            r.UidValidity = Manifest.uid_validity;
            r.SeenCount = Manifest.seen?.Count ?? 0;

            // ---- Records: pointer index vs. disk ----
            var indexedAbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            r.PointerIndexCount = Manifest.record_paths?.Count ?? 0;
            if (Manifest.record_paths != null)
            {
                long total = r.PointerIndexCount;
                long done = 0;
                int step = deep ? 50 : 1000;
                foreach (var kv in Manifest.record_paths)
                {
                    string abs = Path.Combine(ArchiveFolder, kv.Value);
                    try { indexedAbs.Add(Path.GetFullPath(abs)); } catch { }

                    if (!File.Exists(abs)) { r.RecordsMissing++; }
                    else if (deep)
                    {
                        try
                        {
                            var rec = Archive_Record.FromJson(File.ReadAllText(abs));
                            if (rec == null) r.RecordsUnreadable++;
                            else VerifyRecordAttachments(rec, r);
                        }
                        catch { r.RecordsUnreadable++; }
                    }

                    done++;
                    if (onProgress != null && done % step == 0) onProgress(done, total);
                }
                onProgress?.Invoke(done, total);
            }

            // ---- Orphan scan + on-disk record count ----
            long onDisk = 0;
            foreach (var file in EnumerateRecordFiles())
            {
                onDisk++;
                string full;
                try { full = Path.GetFullPath(file); } catch { full = file; }
                if (!indexedAbs.Contains(full)) r.OrphanRecordFiles++;
            }
            r.RecordFilesOnDisk = onDisk;

            // ---- Attachment ZIP count on disk ----
            try
            {
                r.AttachmentZipsOnDisk = System.IO.Directory.Exists(AttachmentsFolder)
                    ? System.IO.Directory.EnumerateFiles(AttachmentsFolder, "*.zip").LongCount()
                    : 0;
            }
            catch { /* leave 0 */ }

            // ---- Persist the dated stamp ----
            try { File.WriteAllText(Path.Combine(ArchiveFolder, AccountKey + ".verification.json"), r.ToJson()); }
            catch { /* best-effort */ }

            return r;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Deep-mode check that a record's referenced attachment ZIP exists and opens. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="rec">	The record. </param>
        /// <param name="r">  	The report being accumulated. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private void VerifyRecordAttachments(Archive_Record rec, Verification_Report r)
        {
            bool expectsZip = !string.IsNullOrEmpty(rec.attachment_zip) ||
                              (rec.attachments != null && rec.attachments.Count > 0);
            if (!expectsZip) return;

            string zip = AttachmentZipPath(rec.archive_pointer);
            if (!File.Exists(zip)) { r.AttachmentZipsMissing++; return; }
            try
            {
                using var fs = new FileStream(zip, FileMode.Open, FileAccess.Read);
                using var za = new ZipArchive(fs, ZipArchiveMode.Read);
                _ = za.Entries.Count; // force central-directory read
            }
            catch { r.AttachmentZipsUnreadable++; }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Enumerates record JSON file paths (excludes manifest + verification stamps). </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <returns>	The record file paths. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private IEnumerable<string> EnumerateRecordFiles()
        {
            if (!System.IO.Directory.Exists(ArchiveFolder)) yield break;
            foreach (var file in System.IO.Directory.EnumerateFiles(ArchiveFolder, "*.json", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.EndsWith(".verification.json", StringComparison.OrdinalIgnoreCase)) continue;
                yield return file;
            }
        }

        // ---- Manifest rebuild (self-healing fallback) --------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Rebuilds the manifest (counts, dedup set, pointer index, ranges) from the on-disk record
        /// tree — the authoritative source — and clears the now-stale journal.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="onProgress">	Optional (processed, total) callback for a progress indicator. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void RebuildManifestFromTree(Action<long, long> onProgress = null)
        {
            long total = onProgress != null ? EnumerateRecordFiles().LongCount() : 0;

            var fresh = new Archive_Manifest
            {
                account_key = AccountKey,
                email = _cfg.email,
                next_pointer = _cfg.archive_pointer_starting_increment ?? 1212,
            };
            long maxPtr = fresh.next_pointer - 1;
            foreach (var rec in EnumerateRecords())
            {
                if (!string.IsNullOrEmpty(rec.gm_msgid))
                    fresh.seen[rec.gm_msgid] = rec.archive_pointer;
                if (!string.IsNullOrEmpty(rec.record_path))
                    fresh.record_paths[rec.archive_pointer] = rec.record_path;
                fresh.message_count++;
                fresh.attachment_count += rec.attachments?.Count ?? 0;
                if (rec.archive_pointer > maxPtr) maxPtr = rec.archive_pointer;
                if (rec.uid != 0 && rec.uid > fresh.high_water_uid) fresh.high_water_uid = rec.uid;
                UpdateRange(fresh, rec.date);
                if (onProgress != null && fresh.message_count % 250 == 0) onProgress(fresh.message_count, total);
            }
            fresh.next_pointer = maxPtr + 1;
            onProgress?.Invoke(fresh.message_count, total);

            lock (_sync)
            {
                Manifest = fresh;
                SaveManifestLocked();
                _journal.Truncate();
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Wipes and rebuilds the Lucene index from the authoritative record tree, re-extracting
        /// attachment text from the stored ZIPs. Use when the index has drifted from the records.
        /// Returns the number of records reindexed.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="onProgress">	Optional (processed, total) callback for a progress indicator. </param>
        ///
        /// <returns>	The count of reindexed records. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public long RebuildIndexFromTree(Action<long, long> onProgress = null)
        {
            long total = onProgress != null ? EnumerateRecordFiles().LongCount() : 0;

            Index.DeleteAll();
            long n = 0;
            foreach (var rec in EnumerateRecords())
            {
                string attText = (_cfg.index_email_attachments ?? true) ? ExtractStoredAttachmentText(rec) : null;
                IndexRecord(rec, attText);
                n++;
                if (onProgress != null && n % 100 == 0) onProgress(n, total);
            }
            Index.Commit();
            onProgress?.Invoke(n, total);
            return n;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Re-extracts indexable attachment text from a record's stored ZIP (best-effort; null when
        /// there is nothing to extract or the ZIP is absent).
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="rec">	The record. </param>
        ///
        /// <returns>	The concatenated extracted text, or null. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private string ExtractStoredAttachmentText(Archive_Record rec)
        {
            if (rec.attachments == null || rec.attachments.Count == 0) return null;
            string zip = AttachmentZipPath(rec.archive_pointer);
            if (!File.Exists(zip)) return null;

            var sb = new StringBuilder();
            try
            {
                using var fs = new FileStream(zip, FileMode.Open, FileAccess.Read);
                using var za = new ZipArchive(fs, ZipArchiveMode.Read);
                foreach (var entry in za.Entries)
                {
                    using var es = entry.Open();
                    using var ms = new MemoryStream();
                    es.CopyTo(ms);
                    // Content type is unknown on rebuild; the extractor sniffs by extension.
                    string text = Attachment_Text_Extractor.Extract(entry.Name, null, ms.ToArray());
                    if (!string.IsNullOrEmpty(text)) sb.Append(text).Append('\n');
                }
            }
            catch { /* best-effort */ }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Updates the range. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="man">	The manager. </param>
        /// <param name="iso">	The ISO. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static void UpdateRange(Archive_Manifest man, string iso)
        {
            if (!DateTimeOffset.TryParse(iso, out var d)) return;
            if (string.IsNullOrEmpty(man.earliest_date) ||
                (DateTimeOffset.TryParse(man.earliest_date, out var e) && d < e))
                man.earliest_date = iso;
            if (string.IsNullOrEmpty(man.latest_date) ||
                (DateTimeOffset.TryParse(man.latest_date, out var l) && d > l))
                man.latest_date = iso;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged
        /// resources.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void Dispose()
        {
            Index?.Commit();
            Index?.Dispose();
            _journal?.Dispose();
        }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Snapshot of archive consistency + resume position, produced by
    /// <see cref="Archive_Store.AnalyzeContinuation"/> and rendered by the menu subsystem.
    /// </summary>
    ///
    /// <remarks>	I Volt, 7/2/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public sealed class Continuation_Report
    {
        /// <summary>	Messages the manifest believes are archived. </summary>
        public long ManifestMessageCount { get; set; }
        /// <summary>	Attachments the manifest believes are archived. </summary>
        public long ManifestAttachmentCount { get; set; }
        /// <summary>	Record JSON files actually present on disk (raw parseable records). </summary>
        public long DiskRecordCount { get; set; }
        /// <summary>	Distinct non-empty Gmail message-ids among the records. </summary>
        public long UniqueMessageIds { get; set; }
        /// <summary>	Records with no Gmail message-id (each indexed on its own). </summary>
        public long RecordsWithoutId { get; set; }
        /// <summary>	Documents actually present in the Lucene index. </summary>
        public long IndexDocCount { get; set; }
        /// <summary>	Distinct unresolved entries in the failure log. </summary>
        public int UnresolvedFailures { get; set; }
        /// <summary>	Records recovered from the journal on the most recent load. </summary>
        public long JournalRecoveredOnLoad { get; set; }
        /// <summary>	Journal lines not yet folded into a snapshot. </summary>
        public int PendingJournalEntries { get; set; }
        /// <summary>	Next archive pointer to assign. </summary>
        public long NextPointer { get; set; }
        /// <summary>	Highest processed IMAP UID (resume optimization). </summary>
        public uint HighWaterUid { get; set; }
        /// <summary>	UIDVALIDITY the high-water UID belongs to. </summary>
        public uint UidValidity { get; set; }
        /// <summary>	Earliest archived message date (ISO 8601). </summary>
        public string EarliestDate { get; set; }
        /// <summary>	Latest archived message date (ISO 8601). </summary>
        public string LatestDate { get; set; }

        /// <summary>
        /// Duplicate record files that share a Gmail id (from interrupted runs). The index keeps one
        /// document per id by design, so these are the difference between raw records and index docs.
        /// </summary>
        public long DuplicateRecords => System.Math.Max(0, DiskRecordCount - UniqueMessageIds - RecordsWithoutId);

        /// <summary>
        /// How many documents a correctly-built index holds: one per distinct Gmail id, plus one for
        /// each record that has no id. This — NOT the raw record count — is what the index is compared
        /// against, because indexing dedups by Gmail id.
        /// </summary>
        public long ExpectedIndexDocs => UniqueMessageIds + RecordsWithoutId;

        /// <summary>	On-disk records minus manifest count. Positive ⇒ manifest is behind the tree. </summary>
        public long ManifestVsDiskDrift => DiskRecordCount - ManifestMessageCount;
        /// <summary>	Index docs minus the expected (deduped) count. Non-zero ⇒ index is out of step. </summary>
        public long IndexDrift => IndexDocCount - ExpectedIndexDocs;

        /// <summary>	True when the manifest and the (deduped) index both agree with the record tree. </summary>
        public bool IsConsistent => ManifestVsDiskDrift == 0 && IndexDrift == 0;

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Human-readable recommendation for what to do next. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <returns>	The recommended next action. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public string Recommendation()
        {
            if (ManifestVsDiskDrift != 0)
                return "Manifest is out of step with the record tree (interrupted run). Run 'Rebuild Manifest From Tree' to reconcile counts and the dedup set.";
            if (IndexDrift != 0)
                return "Search index is out of step with the records. Run 'Rebuild Search Index From Tree'.";
            if (UnresolvedFailures > 0)
                return $"Consistent, but {UnresolvedFailures} message(s) failed to archive. Use Operations -> 'Retry Errors Found in Logs'.";
            if (DuplicateRecords > 0)
                return $"Consistent. Note: {DuplicateRecords} duplicate record file(s) share a Gmail id (leftovers from interrupted runs). The index correctly keeps one copy of each, so search is unaffected; the extra files are harmless.";
            return "Consistent. Use 'Continue Processing' -> Resume (or 'New since last run') to pick up where you left off.";
        }
    }
}
