
using System;
using System.IO;

using Newtonsoft.Json;

namespace IVolt.Core.Email.Gmail
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Result of a file-level verification pass over one account's local archive. Answers the
    /// operator's real question — "is the system confident it can keep downloading from where it left
    /// off?" — by checking that the manifest, journal, search index, record files, and attachment ZIPs
    /// are all present and consistent, and that the resume position is intact. Produced by
    /// <see cref="Archive_Store.VerifyFiles"/>; persisted as {account}.verification.json so each run
    /// updates a visible, dated confidence stamp.
    /// </summary>
    ///
    /// <remarks>	I Volt, 7/3/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public sealed class Verification_Report
    {
        // ---- context ----
        [JsonProperty("account_key")] public string AccountKey { get; set; }
        [JsonProperty("archive_folder")] public string ArchiveFolder { get; set; }
        [JsonProperty("verified_utc")] public string VerifiedUtc { get; set; }
        [JsonProperty("deep")] public bool Deep { get; set; }

        // ---- integrity ----
        [JsonProperty("manifest_file_exists")] public bool ManifestFileExists { get; set; }
        [JsonProperty("manifest_readable")] public bool ManifestReadable { get; set; }
        [JsonProperty("journal_readable")] public bool JournalReadable { get; set; }
        [JsonProperty("pending_journal_entries")] public int PendingJournalEntries { get; set; }
        [JsonProperty("index_openable")] public bool IndexOpenable { get; set; }
        [JsonProperty("index_doc_count")] public long IndexDocCount { get; set; }

        // ---- records ----
        [JsonProperty("pointer_index_count")] public long PointerIndexCount { get; set; }
        [JsonProperty("record_files_on_disk")] public long RecordFilesOnDisk { get; set; }
        [JsonProperty("records_missing")] public long RecordsMissing { get; set; }
        [JsonProperty("records_unreadable")] public long RecordsUnreadable { get; set; }
        [JsonProperty("orphan_record_files")] public long OrphanRecordFiles { get; set; }

        // ---- attachments ----
        [JsonProperty("attachment_zips_on_disk")] public long AttachmentZipsOnDisk { get; set; }
        [JsonProperty("attachment_zips_missing")] public long AttachmentZipsMissing { get; set; }
        [JsonProperty("attachment_zips_unreadable")] public long AttachmentZipsUnreadable { get; set; }

        // ---- resume position ----
        [JsonProperty("manifest_message_count")] public long ManifestMessageCount { get; set; }
        [JsonProperty("next_pointer")] public long NextPointer { get; set; }
        [JsonProperty("high_water_uid")] public uint HighWaterUid { get; set; }
        [JsonProperty("uid_validity")] public uint UidValidity { get; set; }
        [JsonProperty("seen_count")] public long SeenCount { get; set; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// True when the resume metadata needed to continue safely is intact: the manifest loaded, a
        /// valid next pointer exists, the search index is usable, and (for a non-empty archive) the
        /// dedup set is populated so already-downloaded mail is skipped.
        /// </summary>
        ///
        /// <value>	True if the system can continue downloading. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        [JsonProperty("can_continue")]
        public bool CanContinue =>
            ManifestReadable
            && IndexOpenable
            && NextPointer > 0
            && (ManifestMessageCount == 0 || SeenCount > 0);

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	True when it can continue but something drifted and repair is advisable. </summary>
        ///
        /// <value>	True if there are non-fatal warnings. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        [JsonProperty("has_warnings")]
        public bool HasWarnings =>
            RecordsMissing > 0
            || RecordsUnreadable > 0
            || OrphanRecordFiles > 0
            || AttachmentZipsMissing > 0
            || AttachmentZipsUnreadable > 0
            || RecordFilesOnDisk != ManifestMessageCount
            || PendingJournalEntries > 0;

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	The single-line confidence verdict. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <returns>	The verdict string. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public string Verdict()
        {
            if (!CanContinue) return "✗ NOT SAFE — repair before continuing";
            if (HasWarnings) return "! CONFIDENT WITH WARNINGS — safe to continue (repair recommended)";
            return "✓ CONFIDENT — safe to continue";
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Serializes the report for the persisted verification stamp. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <returns>	This report as indented JSON. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Renders the report as an operator-friendly console block. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="o">	The output writer. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void Render(TextWriter o)
        {
            o.WriteLine();
            o.WriteLine("  ===== FILE VERIFICATION =====");
            o.WriteLine($"    Account: {AccountKey}    Mode: {(Deep ? "DEEP (opened every file)" : "QUICK (existence + integrity)")}");
            o.WriteLine($"    Archive: {ArchiveFolder}");

            o.WriteLine("    Integrity");
            o.WriteLine($"      {Tag(ManifestReadable)} Manifest readable{(ManifestFileExists ? "" : " (no snapshot file yet — using in-memory/journal)")}");
            o.WriteLine($"      {Tag(JournalReadable)} Recovery journal readable ({PendingJournalEntries} entr{(PendingJournalEntries == 1 ? "y" : "ies")} pending fold)");
            o.WriteLine($"      {Tag(IndexOpenable)} Search index openable ({IndexDocCount} document(s))");

            o.WriteLine("    Records");
            o.WriteLine($"      {Tag(RecordsMissing == 0)} Pointer index: {PointerIndexCount}    On disk: {RecordFilesOnDisk}    Missing: {RecordsMissing}");
            o.WriteLine($"      {Tag(OrphanRecordFiles == 0)} Orphan record files (on disk, not indexed): {OrphanRecordFiles}");
            if (Deep)
                o.WriteLine($"      {Tag(RecordsUnreadable == 0)} Unreadable record files: {RecordsUnreadable}");

            o.WriteLine("    Attachments");
            o.WriteLine($"      {Tag(true)} Attachment ZIPs on disk: {AttachmentZipsOnDisk}");
            if (Deep)
            {
                o.WriteLine($"      {Tag(AttachmentZipsMissing == 0)} Referenced ZIPs missing: {AttachmentZipsMissing}");
                o.WriteLine($"      {Tag(AttachmentZipsUnreadable == 0)} Unreadable ZIPs: {AttachmentZipsUnreadable}");
            }

            o.WriteLine("    Resume position");
            o.WriteLine($"      Next pointer : {NextPointer}");
            o.WriteLine($"      High-water   : UID > {HighWaterUid} (UIDVALIDITY {UidValidity})");
            o.WriteLine($"      Dedup set    : {SeenCount} message id(s) — already-downloaded mail will be skipped");

            o.WriteLine("  --------------------------------------------------------");
            o.WriteLine($"    VERDICT: {Verdict()}");
            o.WriteLine($"    Verified: {VerifiedUtc}");
            if (!CanContinue)
                o.WriteLine("    → Run Operations → 'Rebuild Manifest From Tree' (and re-verify) before your next download.");
            else if (HasWarnings)
                o.WriteLine("    → You can continue now; 'Rebuild Manifest/Index From Tree' will clear the warnings.");
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	PASS/FAIL tag. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="ok">	True for pass. </param>
        ///
        /// <returns>	The tag. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static string Tag(bool ok) => ok ? "[✓ PASS]" : "[✗ FAIL]";
    }
}
