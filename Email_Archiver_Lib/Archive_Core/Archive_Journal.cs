
using System;
using System.Collections.Generic;
using System.IO;

using Newtonsoft.Json;

namespace IVolt.Core.Email.Gmail
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Append-only, crash-durable journal of messages archived since the last full manifest snapshot.
    /// One JSON object per line, flushed on every append. It exists so a run that is killed between
    /// snapshots loses nothing: on the next load the store replays the journal delta on top of the
    /// last snapshot, reconstructing the dedup set, counts, pointer index and UID high-water exactly.
    ///
    /// This replaces the old "rewrite the entire manifest every 25 messages" behaviour: appends are
    /// O(1) and tiny, while the expensive full snapshot happens only at real checkpoints, which then
    /// fold + truncate this journal.
    /// </summary>
    ///
    /// <remarks>	I Volt, 7/2/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public sealed class Archive_Journal : IDisposable
    {
        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	One journaled message. Field names are terse to keep lines compact. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public sealed class Entry
        {
            /// <summary>	Assigned archive pointer. </summary>
            [JsonProperty("p")] public long pointer { get; set; }
            /// <summary>	X-GM-MSGID (dedup key), may be empty. </summary>
            [JsonProperty("m")] public string gm_msgid { get; set; }
            /// <summary>	Record path relative to the archive root. </summary>
            [JsonProperty("r")] public string record_path { get; set; }
            /// <summary>	Attachment count folded into the manifest total. </summary>
            [JsonProperty("a")] public int attachments { get; set; }
            /// <summary>	Message date (ISO 8601) for range maintenance. </summary>
            [JsonProperty("d")] public string date { get; set; }
            /// <summary>	IMAP UID for high-water reconstruction. </summary>
            [JsonProperty("u")] public uint uid { get; set; }
            /// <summary>	UIDVALIDITY the UID belongs to. </summary>
            [JsonProperty("v")] public uint uid_validity { get; set; }
        }

        /// <summary>	(Immutable) full pathname of the journal file. </summary>
        private readonly string _path;
        /// <summary>	Guards the writer and file operations. </summary>
        private readonly object _sync = new object();
        /// <summary>	Lazily-opened append writer (AutoFlush for crash durability). </summary>
        private StreamWriter _writer;

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Constructor. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="archiveFolder">	Pathname of the archive folder. </param>
        /// <param name="accountKey">   	The sanitized account key. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public Archive_Journal(string archiveFolder, string accountKey)
        {
            _path = Path.Combine(archiveFolder, accountKey + ".journal.log");
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Gets the full pathname of the journal file. </summary>
        ///
        /// <value>	The full pathname of the journal file. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public string JournalPath => _path;

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Append one entry and flush. Thread-safe. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <param name="e">	The entry. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void Append(Entry e)
        {
            string line = JsonConvert.SerializeObject(e);
            lock (_sync)
            {
                _writer ??= new StreamWriter(_path, append: true) { AutoFlush = true };
                _writer.WriteLine(line);
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Reads every entry. Tolerant of a torn final line (a crash mid-append). Thread-safe.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <returns>	The journaled entries in append order. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public List<Entry> ReadAll()
        {
            var list = new List<Entry>();
            lock (_sync)
            {
                _writer?.Flush();
                if (!File.Exists(_path)) return list;
                foreach (var line in File.ReadAllLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var e = JsonConvert.DeserializeObject<Entry>(line);
                        if (e != null) list.Add(e);
                    }
                    catch { /* tolerate a partially-written trailing line */ }
                }
            }
            return list;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Count of non-blank journal lines still pending fold. Thread-safe. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ///
        /// <returns>	The pending entry count. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public int Count()
        {
            lock (_sync)
            {
                _writer?.Flush();
                if (!File.Exists(_path)) return 0;
                int n = 0;
                foreach (var line in File.ReadLines(_path))
                    if (!string.IsNullOrWhiteSpace(line)) n++;
                return n;
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Discards the journal after its contents have been folded into a full snapshot. Thread-safe.
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void Truncate()
        {
            lock (_sync)
            {
                _writer?.Dispose();
                _writer = null;
                try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best-effort */ }
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Flushes and closes the writer. </summary>
        ///
        /// <remarks>	I Volt, 7/2/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void Dispose()
        {
            lock (_sync)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
