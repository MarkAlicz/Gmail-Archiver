
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

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
		/// <summary>	everything in the folder. </summary>
		FullExtract,
		/// <summary>	everything, but manifest dedup skips already-archived. </summary>
		Resume,
		/// <summary>	messages within [Since, Before]. </summary>
		DateRange,
		/// <summary>	messages with UID above the stored high-water mark. </summary>
		UidRange,
		/// <summary>	a specific Gmail label / folder. </summary>
		Label
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
	/// Archival fetch pipeline over MailKit transport. A control connection enumerates candidate UIDs
	/// and batch-fetches their metadata (one round trip) so duplicates are filtered before any body is
	/// pulled. The remaining bodies are pulled by a small pool of IMAP connections (producers) and
	/// handed to a pool of parse/store workers (consumers) via a bounded queue — the CPU-bound custom
	/// MIME parse, attachment text extraction, ZIP and index writes all run in parallel.
	///
	/// MailKit's ImapClient is not thread-safe, so each producer owns its own connection; the Lucene
	/// writer and the Archive_Store are thread-safe. Parse failures NEVER lose a message — they are
	/// archived raw with metadata; only genuine transport failures are retried and, on give-up, logged
	/// for later retry via the menu subsystem. Every stored message is journaled, so an interrupted
	/// run resumes exactly.
	/// </summary>
	///
	/// <remarks>	I Volt, 6/30/2026. </remarks>
	////////////////////////////////////////////////////////////////////////////////////////////////////

	public sealed class Gmail_Fetch_Engine
	{
		/// <summary>	Message-count cadence at which a checkpoint is *considered*. </summary>
		private const int CheckpointEvery = 200;

		/// <summary>
		/// Minimum wall-clock gap between checkpoints. A checkpoint writes the full manifest snapshot
		/// (the seen + record_paths maps, both O(n)); firing purely on message count degrades toward
		/// O(n²) total writes on a fast, large run. The append-only journal already makes every stored
		/// message durable, so checkpoints are compaction only and can be time-bounded.
		/// </summary>
		private static readonly long CheckpointMinIntervalTicks = TimeSpan.FromSeconds(15).Ticks;

		/// <summary>	UTC ticks of the last checkpoint (CAS-guarded across workers). </summary>
		private long _lastCheckpointTicks;

		/// <summary>	(Immutable) the configuration. </summary>
		private readonly Configuration_Definition_Container _cfg;
		/// <summary>	(Immutable) the store. </summary>
		private readonly Archive_Store _store;
		/// <summary>	(Immutable) the out. </summary>
		private readonly TextWriter _out;

		/// <summary>	Throttle accounting (rolling one-minute window), shared across all producers. </summary>
		private readonly object _throttleLock = new object();
		/// <summary>	The emails this minute. </summary>
		private int _emailsThisMinute;
		/// <summary>	The bytes this minute. </summary>
		private long _bytesThisMinute;
		/// <summary>	The window start. </summary>
		private DateTime _windowStart = DateTime.UtcNow;

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Constructor. </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
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
		/// <summary>	A unit of work handed from a fetch producer to a parse/store worker. </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private sealed class Work_Item
		{
			/// <summary>	Exact wire octets of the message. </summary>
			public byte[] Raw;
			/// <summary>	Metadata summary fetched on the control connection. </summary>
			public IMessageSummary Summary;
			/// <summary>	UIDVALIDITY of the mailbox the body came from. </summary>
			public uint UidValidity;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Opens a connected, authenticated IMAP client. Caller disposes. </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
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
		/// Runs the archival pipeline for the given options. Returns count of newly archived messages.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		///
		/// <param name="opts">	Options for controlling the operation. </param>
		/// <param name="ct">  	Cancellation token; on cancel, producers stop pulling new messages,
		/// 					already-fetched messages are still stored, and a final checkpoint runs. </param>
		///
		/// <returns>	A long. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public long Run(Fetch_Options opts, CancellationToken ct = default)
		{
			var failures = new Failure_Log(_store.ArchiveFolder, _store.AccountKey);

			// --- Control connection: select UIDs + batch-fetch metadata, then dedup. ----------
			IList<IMessageSummary> todo;
			uint uidValidity;
			using (var control = Connect())
			{
				var folder = control.GetFolder(opts.Label);
				folder.Open(FolderAccess.ReadOnly);
				uidValidity = folder.UidValidity;
				_out.WriteLine($"Opened '{opts.Label}': {folder.Count} messages, UIDVALIDITY {uidValidity}.");

				IList<UniqueId> uids = SelectUids(folder, opts);
				_out.WriteLine($"Scope '{opts.Scope}' selected {uids.Count} candidate message(s).");

				if (uids.Count == 0) { folder.Close(); control.Disconnect(true); return 0; }

				// One round trip for all metadata (vs. one-per-message previously).
				var summaries = folder.Fetch(uids,
					MessageSummaryItems.UniqueId |
					MessageSummaryItems.Flags |
					MessageSummaryItems.InternalDate |
					MessageSummaryItems.Size |
					MessageSummaryItems.GMailMessageId |
					MessageSummaryItems.GMailThreadId |
					MessageSummaryItems.GMailLabels);

				todo = summaries.Where(s => !_store.AlreadyArchived(s.GMailMessageId?.ToString())).ToList();
				_out.WriteLine($"After dedup: {todo.Count} message(s) to archive.");
				folder.Close();
				control.Disconnect(true);
			}

			if (todo.Count == 0) { _store.Checkpoint(); return 0; }

			// --- Fan-out sizing (0 or unset ⇒ auto). ------------------------------------------
			long? fcCfg = _cfg.performance?.fetch_connections;
			int fetchConns = (fcCfg.HasValue && fcCfg.Value > 0) ? (int)fcCfg.Value : 4;
			fetchConns = Math.Min(Clamp(fetchConns, 1, 15), todo.Count);

			long? pwCfg = _cfg.performance?.parse_workers;
			int parseWorkers = (pwCfg.HasValue && pwCfg.Value > 0)
				? (int)pwCfg.Value
				: Math.Min(Environment.ProcessorCount, 8);
			parseWorkers = Clamp(parseWorkers, 1, 64);
			_out.WriteLine($"Pipeline: {fetchConns} fetch connection(s) -> {parseWorkers} parse/store worker(s).");

			// Ensure the shared Lucene writer is constructed once before workers race on it.
			_store.Index.OpenForWrite();

			// --- Bounded hand-off queue (backpressure caps RAM to ~queue depth of raw bodies). --
			int capacity = Math.Max(parseWorkers * 4, 8);
			using var queue = new BlockingCollection<Work_Item>(capacity);

			// --- Live dashboard (progress, msg/s, attachments, bandwidth, ETA). ----------------
			var monitor = new Download_Monitor(_out, todo.Count, fetchConns, parseWorkers, () => queue.Count);

			long archived = 0;
			int maxRetries = (int)(_cfg.performance?.max_retries ?? 3);
			Interlocked.Exchange(ref _lastCheckpointTicks, DateTime.UtcNow.Ticks);

			monitor.Start();
			try
			{
				// --- Consumers: parse + store. ------------------------------------------------
				var workers = new Task[parseWorkers];
				for (int w = 0; w < parseWorkers; w++)
				{
					workers[w] = Task.Run(() =>
					{
						foreach (var item in queue.GetConsumingEnumerable())
						{
							try
							{
								var msg = ParseDefensively(item.Raw, item.Summary.UniqueId, monitor.Log);
								ApplyMetadata(msg, item.UidValidity, item.Summary);
								_store.StoreMessage(msg);
								monitor.OnArchived(CountAttachments(msg));

								long done = Interlocked.Increment(ref archived);
								if (done % CheckpointEvery == 0) TryCheckpoint();
							}
							catch (Exception ex)
							{
								// Store-side failure (disk/index). The body is lost from this run but
								// the UID is logged so it can be retried.
								monitor.OnFailure();
								failures.Record(item.Summary.UniqueId.Id, item.UidValidity, opts.Label,
												"store failed: " + ex.Message);
								monitor.Log($"  ! Store failed for UID {item.Summary.UniqueId}: {ex.Message}");
							}
						}
					});
				}

				// --- Producers: one IMAP connection each, pulling bodies for a UID slice. ------
				var slices = Partition(todo, fetchConns);
				var producers = new Task[slices.Count];
				for (int p = 0; p < slices.Count; p++)
				{
					var slice = slices[p];
					producers[p] = Task.Run(() => Produce(slice, opts, uidValidity, maxRetries, queue, failures, monitor, ct));
				}

				Task.WaitAll(producers);
				queue.CompleteAdding();
				Task.WaitAll(workers);
			}
			finally
			{
				monitor.Stop();
			}

			// --- Final durable checkpoint. ----------------------------------------------------
			_store.Checkpoint();
			_out.WriteLine($"Run complete. Newly archived: {Interlocked.Read(ref archived)}.");
			return Interlocked.Read(ref archived);
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Counts the attachment parts that would be archived for this message. </summary>
		///
		/// <remarks>	I Volt, 7/3/2026. </remarks>
		///
		/// <param name="msg">	The message. </param>
		///
		/// <returns>	The attachment count. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private int CountAttachments(Gmail_IMAP_Message_Container msg)
		{
			if (!(_cfg.archive_attachments ?? true) || msg.Parts == null) return 0;
			int c = 0;
			foreach (var p in msg.Parts)
				if (p.IsAttachment || !string.IsNullOrEmpty(p.FileName)) c++;
			return c;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Fires a durable checkpoint at most once per <see cref="CheckpointMinIntervalTicks"/>, claimed
		/// via CAS so exactly one worker performs it. Between checkpoints the journal keeps every stored
		/// message recoverable, so throttling checkpoint frequency costs nothing but avoids repeatedly
		/// rewriting the ever-growing manifest snapshot.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/3/2026. </remarks>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private void TryCheckpoint()
		{
			long now = DateTime.UtcNow.Ticks;
			long prev = Interlocked.Read(ref _lastCheckpointTicks);
			if (now - prev < CheckpointMinIntervalTicks) return;                       // too soon
			if (Interlocked.CompareExchange(ref _lastCheckpointTicks, now, prev) != prev) return; // lost the race
			_store.Checkpoint();
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Body-pull loop for one producer connection: fetches raw octets for its UID slice (with
		/// transport retry), throttles, and enqueues work. Never marks \Seen.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		///
		/// <param name="slice">	  	The UID summaries assigned to this connection. </param>
		/// <param name="opts">		  	The run options (for the folder name + failure logging). </param>
		/// <param name="uidValidity">	UIDVALIDITY captured on the control connection. </param>
		/// <param name="maxRetries"> 	Transport retry budget per message. </param>
		/// <param name="queue">	  	The hand-off queue. </param>
		/// <param name="failures">   	The failure log. </param>
		/// <param name="monitor">    	The live dashboard (counters + interleaved logging). </param>
		/// <param name="ct">		   	Cancellation token; stops pulling new messages when signalled. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private void Produce(IList<IMessageSummary> slice, Fetch_Options opts, uint uidValidity,
							  int maxRetries, BlockingCollection<Work_Item> queue, Failure_Log failures,
							  Download_Monitor monitor, CancellationToken ct)
		{
			int pauseSec = (int)(_cfg.performance?.pause_between_requests_in_seconds ?? 11);

			ImapClient client = null;
			IMailFolder folder = null;
			try
			{
				client = Connect();
				folder = client.GetFolder(opts.Label);
				folder.Open(FolderAccess.ReadOnly);

				foreach (var sum in slice)
				{
					if (ct.IsCancellationRequested) break;   // stop fetching; queued work still stores
					byte[] raw = null;
					for (int attempt = 1; attempt <= maxRetries && raw == null; attempt++)
					{
						try { raw = GetRaw(folder, sum.UniqueId); }
						catch (Exception ex)
						{
							if (attempt == maxRetries)
							{
								monitor.OnFailure();
								monitor.Log($"  x Giving up on UID {sum.UniqueId} after {maxRetries} attempts: {ex.Message}");
								failures.Record(sum.UniqueId.Id, uidValidity, opts.Label,
												"max retries exceeded: " + ex.Message);
							}
							else
							{
								monitor.OnRetry();
								Thread.Sleep(1000 * attempt); // simple backoff
							}
						}
					}
					if (raw == null) continue;

					monitor.OnFetched(raw.LongLength);           // bandwidth accounting
					Throttle(sum.Size ?? 0, monitor.Log);        // global rate ceiling
					queue.Add(new Work_Item { Raw = raw, Summary = sum, UidValidity = uidValidity });
					if (pauseSec > 0) Thread.Sleep(TimeSpan.FromSeconds(pauseSec)); // per-connection spacing
				}
			}
			catch (Exception ex)
			{
				monitor.Log($"  ! Fetch connection failed: {ex.Message}");
			}
			finally
			{
				try { folder?.Close(); } catch { }
				try { client?.Disconnect(true); } catch { }
				client?.Dispose();
			}
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Pulls exact wire octets for a UID without marking \Seen. </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		///
		/// <param name="folder">	The folder. </param>
		/// <param name="uid">   	The UID. </param>
		///
		/// <returns>	The raw octets. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static byte[] GetRaw(IMailFolder folder, UniqueId uid)
		{
			using var stream = folder.GetStream(uid, string.Empty, 0, int.MaxValue);
			using var ms = new MemoryStream();
			stream.CopyTo(ms);
			return ms.ToArray();
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Re-fetches and re-processes a single message by UID. Used by the "Retry Errors Found in Logs"
		/// menu item. Ignores the dedup set (a retry is an explicit reprocess request). Returns true if
		/// the message was archived; false if it no longer exists in the folder.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		///
		/// <param name="folder">	Pathname of the folder. </param>
		/// <param name="uid">   	The UID. </param>
		///
		/// <returns>	True if it succeeds, false if it fails. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public bool RetryOne(IMailFolder folder, UniqueId uid)
		{
			var summary = folder.Fetch(new[] { uid },
				MessageSummaryItems.UniqueId |
				MessageSummaryItems.Flags |
				MessageSummaryItems.InternalDate |
				MessageSummaryItems.Size |
				MessageSummaryItems.GMailMessageId |
				MessageSummaryItems.GMailThreadId |
				MessageSummaryItems.GMailLabels).FirstOrDefault();

			if (summary == null) return false; // moved/deleted since the failure was logged

			byte[] raw = GetRaw(folder, uid);
			var msg = ParseDefensively(raw, uid, _out.WriteLine);
			ApplyMetadata(msg, folder.UidValidity, summary);
			_store.StoreMessage(msg);
			return true;
		}

		// ---- Single-message processing (defensive) -----------------------

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Parses raw octets with the custom MIME parser; on any failure degrades to a minimal
		/// raw+hash container so the message is archived rather than lost.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		///
		/// <param name="raw">	The raw octets. </param>
		/// <param name="uid">	The UID (for diagnostics only). </param>
		/// <param name="log">	Sink for the degraded-parse notice (monitor.Log during a run). </param>
		///
		/// <returns>	A populated container. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private Gmail_IMAP_Message_Container ParseDefensively(byte[] raw, UniqueId uid, Action<string> log)
		{
			try
			{
				// Custom parser owns SHA-256 + MIME decode.
				return Gmail_IMAP_Message_Processors.Process(raw);
			}
			catch (Exception ex)
			{
				// Catch-all: a malformed header must not lose the message. We still hold the
				// raw octets and can hash them; archive a minimal record with metadata only.
				log?.Invoke($"    (parse degraded for UID {uid}: {ex.Message}; archiving raw)");
				return BuildMinimalContainer(raw);
			}
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Minimal container when full parse fails: raw bytes + hash, metadata applied later.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		///
		/// <param name="raw">	The raw. </param>
		///
		/// <returns>	A Gmail_IMAP_Message_Container. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static Gmail_IMAP_Message_Container BuildMinimalContainer(byte[] raw)
		{
			var c = new Gmail_IMAP_Message_Container
			{
				RawBytes = raw,
				RawSha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
			};
			c.ParseWarnings.Add("Full parse failed; archived as raw with metadata only.");
			return c;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Applies IMAP-layer + Gmail metadata from the MailKit summary to the container.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		///
		/// <param name="msg">		  	The message. </param>
		/// <param name="uidValidity">	UIDVALIDITY of the source mailbox. </param>
		/// <param name="summary">	  	The summary. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static void ApplyMetadata(Gmail_IMAP_Message_Container msg, uint uidValidity, IMessageSummary summary)
		{
			msg.Uid = summary.UniqueId.Id;
			msg.UidValidity = uidValidity;
			msg.Rfc822Size = summary.Size ?? 0;
			msg.InternalDate = summary.InternalDate;
			msg.GmMsgId = summary.GMailMessageId ?? 0;
			msg.GmThrId = summary.GMailThreadId ?? 0;

			if (summary.GMailLabels != null)
				foreach (var label in summary.GMailLabels)
					msg.GmLabels.Add(label);

			if (summary.Flags.HasValue)
			{
				var f = summary.Flags.Value;
				if (f.HasFlag(MessageFlags.Seen)) msg.Flags.Add("\\Seen");
				if (f.HasFlag(MessageFlags.Answered)) msg.Flags.Add("\\Answered");
				if (f.HasFlag(MessageFlags.Flagged)) msg.Flags.Add("\\Flagged");
				if (f.HasFlag(MessageFlags.Deleted)) msg.Flags.Add("\\Deleted");
				if (f.HasFlag(MessageFlags.Draft)) msg.Flags.Add("\\Draft");
			}
		}

		// ---- Scope -> UID set --------------------------------------------

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Select uids. </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
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
					var range = new UniqueIdRange(new UniqueId(folder.UidValidity, hw + 1), UniqueId.MaxValue);
					return folder.Search(SearchQuery.Uids(range));

				case Fetch_Scope.FullExtract:
				case Fetch_Scope.Resume:
				case Fetch_Scope.Label:
				default:
					return folder.Search(SearchQuery.All); // Resume dedups later via manifest
			}
		}

		// ---- Fan-out helpers ---------------------------------------------

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Splits a list into up to <paramref name="parts"/> contiguous slices. </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		///
		/// <param name="items">	The items. </param>
		/// <param name="parts">	The desired slice count. </param>
		///
		/// <returns>	The non-empty slices. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static List<IList<IMessageSummary>> Partition(IList<IMessageSummary> items, int parts)
		{
			var result = new List<IList<IMessageSummary>>();
			if (parts < 1) parts = 1;
			int per = (items.Count + parts - 1) / parts;   // ceil
			for (int i = 0; i < items.Count; i += per)
				result.Add(items.Skip(i).Take(per).ToList());
			return result;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Clamp. </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		///
		/// <param name="v">  	The value. </param>
		/// <param name="lo"> 	The low bound. </param>
		/// <param name="hi"> 	The high bound. </param>
		///
		/// <returns>	The clamped value. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

		// ---- Throttling (thread-safe, global) ----------------------------

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Enforces the global per-minute email and attachment-byte ceilings across every producer
		/// connection. When a ceiling is hit the calling producer sleeps out the remainder of the
		/// window; other producers keep their own per-connection spacing independently.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/2/2026. </remarks>
		///
		/// <param name="messageBytes">	The message in bytes. </param>
		/// <param name="log">		   	Sink for the rate-ceiling notice (monitor.Log during a run). </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private void Throttle(long messageBytes, Action<string> log)
		{
			int maxEmails = (int)(_cfg.performance?.max_emails_per_minute ?? 100);
			long maxAttMB = _cfg.performance?.max_attachment_size_in_MB_per_minute ?? 50;

			TimeSpan wait = TimeSpan.Zero;
			lock (_throttleLock)
			{
				if ((DateTime.UtcNow - _windowStart).TotalSeconds >= 60)
				{
					_windowStart = DateTime.UtcNow;
					_emailsThisMinute = 0;
					_bytesThisMinute = 0;
				}

				_emailsThisMinute++;
				_bytesThisMinute += messageBytes;

				bool overEmail = _emailsThisMinute >= maxEmails;
				bool overBytes = _bytesThisMinute >= maxAttMB * 1024L * 1024L;
				if (overEmail || overBytes)
				{
					double remaining = 60 - (DateTime.UtcNow - _windowStart).TotalSeconds;
					if (remaining > 0) wait = TimeSpan.FromSeconds(remaining);
					// Open the next window after the pause so accounting stays honest.
					_windowStart = DateTime.UtcNow.Add(wait);
					_emailsThisMinute = 0;
					_bytesThisMinute = 0;
				}
			}

			if (wait > TimeSpan.Zero)
			{
				log?.Invoke($"  ⧗ Rate ceiling reached; pausing {wait.TotalSeconds:F0}s.");
				Thread.Sleep(wait);
			}
		}
	}
}
