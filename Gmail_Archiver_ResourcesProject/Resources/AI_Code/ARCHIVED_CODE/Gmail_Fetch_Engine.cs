

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

using IVolt.Core.Email.Configuration;

namespace IVolt.Core.Email.Gmail
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>	Scope of a fetch run, chosen by the operator before processing. </summary>
    ///
    /// <remarks>	I Volt, 7/1/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public enum Fetch_Scope
    {
        FullExtract,     // everything in All Mail
        Resume,          // only messages not already in the manifest
        DateRange,       // messages within [Since, Before]
        UidRange,        // messages with UID >= high-water (fast incremental)
        Label            // a specific Gmail label / folder
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>	A fetch options. This class cannot be inherited. </summary>
    ///
    /// <remarks>	I Volt, 7/1/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public sealed class Fetch_Options
    {
        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Gets or sets the scope. </summary>
        ///
        /// <value>	The scope. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public Fetch_Scope Scope { get; set; } = Fetch_Scope.Resume;

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Gets or sets the Date/Time of the since. </summary>
        ///
        /// <value>	The since. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public DateTime? Since { get; set; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Gets or sets the Date/Time of the before. </summary>
        ///
        /// <value>	The before. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public DateTime? Before { get; set; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Gets or sets the label. </summary>
        ///
        /// <value>	The label. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public string Label { get; set; } = "[Gmail]/All Mail";
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Drives the archival fetch loop over MailKit transport. MailKit fetches raw octets
    /// (BODY.PEEK[])
    /// plus native X-GM-* metadata; the custom Gmail_IMAP_Message_Processors performs all parsing
    /// and provenance hashing. Honors every performance setting in the configuration.
    /// </summary>
    ///
    /// <remarks>	I Volt, 6/30/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public sealed class Gmail_Fetch_Engine
    {
        /// <summary>	(Immutable) the configuration. </summary>
        private readonly Configuration_Definition_Container _cfg;
        /// <summary>	(Immutable) the store. </summary>
        private readonly Archive_Store _store;
        /// <summary>	(Immutable) the out. </summary>
        private readonly TextWriter _out;

        /// <summary>	Throttle accounting (rolling one-minute windows). </summary>
        private int _emailsThisMinute;
        /// <summary>	The attachment bytes this minute. </summary>
        private long _attachmentBytesThisMinute;
        /// <summary>	The window start. </summary>
        private DateTime _windowStart = DateTime.UtcNow;

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Constructor. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="cfg">   	The configuration. </param>
        /// <param name="store"> 	The store. </param>
        /// <param name="output">	The output. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public Gmail_Fetch_Engine(Configuration_Definition_Container cfg, Archive_Store store, TextWriter output)
        {
            _cfg = cfg;
            _store = store;
            _out = output;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Opens a connected, authenticated IMAP client. Caller disposes. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <returns>	An ImapClient. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public ImapClient Connect()
        {
            var client = new ImapClient();
            string host = _cfg.server?.imap ?? "imap.gmail.com";
            int port = (int)(_cfg.server?.port ?? 993);
            bool ssl = _cfg.server?.ssl ?? true;

            client.Connect(host, port, ssl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);

            string user = _cfg.email;
            string pass = _cfg.server?.application_specific_password ?? _cfg.password;
            client.Authenticate(user, pass);
            return client;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Runs the archival loop for the given options. Returns count of newly archived messages.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="opts">	Options for controlling the operation. </param>
        ///
        /// <returns>	A long. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public long Run(Fetch_Options opts)
        {
            long archived = 0;
            using var client = Connect();

            var folder = client.GetFolder(opts.Label);
            folder.Open(FolderAccess.ReadOnly);
            _out.WriteLine($"Opened '{opts.Label}': {folder.Count} messages, UIDVALIDITY {folder.UidValidity}.");

            IList<UniqueId> uids = SelectUids(folder, opts);
            _out.WriteLine($"Scope '{opts.Scope}' selected {uids.Count} candidate message(s).");

            int maxRetries = (int)(_cfg.performance?.max_retries ?? 3);

            foreach (var uid in uids)
            {
                // Fetch summary (metadata) first so we can skip duplicates cheaply.
                IMessageSummary summary;
                try
                {
                    summary = folder.Fetch(new[] { uid },
                        MessageSummaryItems.UniqueId |
                        MessageSummaryItems.Flags |
                        MessageSummaryItems.InternalDate |
                        MessageSummaryItems.Size |
                        MessageSummaryItems.GMailMessageId |
                        MessageSummaryItems.GMailThreadId |
                        MessageSummaryItems.GMailLabels).FirstOrDefault();
                }
                catch (Exception ex)
                {
                    _out.WriteLine($"  ! Fetch summary failed for UID {uid}: {ex.Message}");
                    continue;
                }
                if (summary == null) continue;

                string gmId = summary.GMailMessageId?.ToString();
                if (_store.AlreadyArchived(gmId))
                    continue; // dedup

                bool ok = false;
                for (int attempt = 1; attempt <= maxRetries && !ok; attempt++)
                {
                    try
                    {
                        ProcessOne(folder, uid, summary);
                        archived++;
                        ok = true;
                    }
                    catch (Exception ex)
                    {
                        _out.WriteLine($"  ! Attempt {attempt}/{maxRetries} failed for UID {uid}: {ex.Message}");
                        if (attempt == maxRetries)
                            _out.WriteLine($"  x Giving up on UID {uid} after {maxRetries} attempts.");
                        else
                            Thread.Sleep(1000 * attempt); // simple backoff
                    }
                }

                // Persist manifest + index periodically so an interrupt loses little.
                if (archived % 25 == 0)
                {
                    _store.Index.Commit();
                    _store.SaveManifest();
                    if (summary.UniqueId.Id > _store.Manifest.high_water_uid)
                    {
                        _store.Manifest.high_water_uid = summary.UniqueId.Id;
                        _store.Manifest.uid_validity = folder.UidValidity;
                    }
                    _out.WriteLine($"  … {archived} archived (checkpoint).");
                }

                Throttle(summary.Size ?? 0);
            }

            // Final flush.
            _store.Index.Commit();
            _store.Manifest.high_water_uid = Math.Max(_store.Manifest.high_water_uid,
                                                      uids.Count > 0 ? uids.Max(u => u.Id) : _store.Manifest.high_water_uid);
            _store.Manifest.uid_validity = folder.UidValidity;
            _store.SaveManifest();

            folder.Close();
            client.Disconnect(true);
            _out.WriteLine($"Run complete. Newly archived: {archived}.");
            return archived;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Process the one. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="folder"> 	Pathname of the folder. </param>
        /// <param name="uid">	  	The UID. </param>
        /// <param name="summary">	The summary. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private void ProcessOne(IMailFolder folder, UniqueId uid, IMessageSummary summary)
        {
            // Pull the exact wire octets without marking \Seen — provenance-preserving.
            byte[] raw;
            using (var stream = folder.GetStream(uid, string.Empty, 0, int.MaxValue))
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                raw = ms.ToArray();
            }

            // Custom parser owns everything from here (SHA-256 + MIME decode).
            var msg = Gmail_IMAP_Message_Processors.Process(raw);

			// IMAP-layer metadata lives outside the message octets — set it directly on the container.
			msg.Uid = summary.UniqueId.Id;
			msg.UidValidity = folder.UidValidity;
			msg.Rfc822Size = summary.Size ?? 0;
			msg.InternalDate = summary.InternalDate;
			msg.GmMsgId = summary.GMailMessageId ?? 0;
			msg.GmThrId = summary.GMailThreadId ?? 0;

			// Gmail labels
			if (summary.GMailLabels != null)
				foreach (var label in summary.GMailLabels)
					msg.GmLabels.Add(label);

			// Precise MailKit MessageFlags -> your Flags set (no string parsing)
			if (summary.Flags.HasValue)
			{
				var f = summary.Flags.Value;
				if (f.HasFlag(MessageFlags.Seen)) msg.Flags.Add("\\Seen");
				if (f.HasFlag(MessageFlags.Answered)) msg.Flags.Add("\\Answered");
				if (f.HasFlag(MessageFlags.Flagged)) msg.Flags.Add("\\Flagged");
				if (f.HasFlag(MessageFlags.Deleted)) msg.Flags.Add("\\Deleted");
				if (f.HasFlag(MessageFlags.Draft)) msg.Flags.Add("\\Draft");
				if (f.HasFlag(MessageFlags.Recent)) msg.Flags.Add("\\Recent");
			}

			_store.StoreMessage(msg);
        }

			////////////////////////////////////////////////////////////////////////////////////////////////////
			/// <summary>
			/// ---- Scope -> UID set -------------------------------------------- HELP APP LOCATION
			/// IVoltArchiver-
			/// https://myaccount.google.com/u/2/apppasswords?rapt=AEjHL4PjaDkMf6gzHM6OHdCb4dCVlGMeUQrTl3ToaphdWjW9LvZnL2uQftNMpsODshDg96nl-fcQ7rJEz4lvgaczAz6Cw3wyAOhHmqRDEKbk24caJZkPrFA.
			/// </summary>
			///
			/// <remarks>	I Volt, 7/1/2026. </remarks>
			///
			/// <param name="folder">	Pathname of the folder. </param>
			/// <param name="opts">  	Options for controlling the operation. </param>
			///
			/// <returns>	A list of. </returns>
			////////////////////////////////////////////////////////////////////////////////////////////////////

			private IList<UniqueId> SelectUids(IMailFolder folder, Fetch_Options opts)
        {
            switch (opts.Scope)
            {
                case Fetch_Scope.DateRange:
                    var q = SearchQuery.All;
                    if (opts.Since.HasValue) q = q.And(SearchQuery.DeliveredAfter(opts.Since.Value));
                    if (opts.Before.HasValue) q = q.And(SearchQuery.DeliveredBefore(opts.Before.Value));
                    return folder.Search(q);

                case Fetch_Scope.UidRange:
                    uint hw = _store.Manifest.high_water_uid;
                    if (folder.UidValidity != _store.Manifest.uid_validity) hw = 0; // validity changed -> full
                    var range = new UniqueIdRange(new UniqueId(folder.UidValidity, hw + 1),
                                                  UniqueId.MaxValue);
                    return folder.Search(SearchQuery.Uids(range));

                case Fetch_Scope.FullExtract:
                case Fetch_Scope.Resume:
                case Fetch_Scope.Label:
                default:
                    return folder.Search(SearchQuery.All); // Resume dedups later via manifest
            }
        }

        // ---- Throttling ---------------------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Throttles the given message bytes. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="messageBytes">	The message in bytes. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private void Throttle(long messageBytes)
        {
            int maxEmails   = (int)(_cfg.performance?.max_emails_per_minute ?? 100);
            long maxAttMB   = _cfg.performance?.max_attachment_size_in_MB_per_minute ?? 50;
            int pauseSec    = (int)(_cfg.performance?.pause_between_requests_in_seconds ?? 11);

            _emailsThisMinute++;
            _attachmentBytesThisMinute += messageBytes;

            // Roll the window.
            if ((DateTime.UtcNow - _windowStart).TotalSeconds >= 60)
            {
                _windowStart = DateTime.UtcNow;
                _emailsThisMinute = 0;
                _attachmentBytesThisMinute = 0;
            }

            bool overEmail = _emailsThisMinute >= maxEmails;
            bool overBytes = _attachmentBytesThisMinute >= maxAttMB * 1024L * 1024L;
            if (overEmail || overBytes)
            {
                double wait = 60 - (DateTime.UtcNow - _windowStart).TotalSeconds;
                if (wait > 0)
                {
                    _out.WriteLine($"  ⧗ Rate ceiling reached; pausing {wait:F0}s.");
                    Thread.Sleep(TimeSpan.FromSeconds(wait));
                }
                _windowStart = DateTime.UtcNow;
                _emailsThisMinute = 0;
                _attachmentBytesThisMinute = 0;
            }

            if (pauseSec > 0) Thread.Sleep(TimeSpan.FromSeconds(pauseSec));
        }
    }
}
