# IVolt Gmail IMAP Archiver

A privacy-first, local-only tool that downloads your Gmail over IMAP and stores it as a durable, searchable archive on your own machine. Nothing leaves your computer: mail, attachments and the search index all live under folders you choose. Built by IVolt (Inspired Voltage).

- **Complete, provenance-preserving capture.** Every message is fetched as its exact wire bytes, SHA-256 hashed, and stored. A message is *never* dropped — if a header is malformed, it is archived raw with its metadata rather than skipped.
- **Resumable and crash-safe.** An append-only journal records every stored message, so an interrupted run resumes exactly where it left off. Dedup is by Gmail message-id.
- **Fast.** A parallel fetch/parse pipeline pulls bodies over several IMAP connections and parses/indexes them across worker threads, with a live progress dashboard.
- **Searchable.** A per-account Lucene.NET index covers senders, subjects, body text, headers, attachment names, and extracted attachment *content* (PDF, Word, Excel, plain text).
- **Verifiable.** A one-command file-verification pass tells you, with a clear GO/NO-GO verdict, whether the archive is intact and safe to continue.
- **Automatable.** A simple script mode runs headless for scheduled backups.
- **Extensible.** Drop-in plugins can run custom logic against a loaded archive.

---

## Requirements

- **Windows, Linux, or macOS.** Credentials are protected at rest with an OS-appropriate scheme: Windows DPAPI (CurrentUser) on Windows, and AES-256-GCM under an owner-only key file on Linux/macOS. See [docs/LINUX_COMPATIBILITY.md](docs/LINUX_COMPATIBILITY.md).
- **.NET 10 SDK** to build (this is a Visual Studio solution; `dotnet build` works on any platform).
- **A Gmail account with 2-Step Verification** and a **16-character app password** (regular passwords won't work for IMAP).

### Generating a Gmail app password

1. Enable 2-Step Verification on your Google account.
2. Visit https://myaccount.google.com/apppasswords
3. Create an app password (any name), and copy the 16 characters.
4. Use that value when the archiver asks for the "app-specific password". It is stored encrypted (Windows DPAPI, or AES on Linux/macOS), never in plain text.

---

## Quick start

```
# Build
dotnet build Gmail_Archiver_Solution.slnx -c Release
# (or open the .slnx in Visual Studio and build)

# Run interactively
Gmail_Archiver.exe
```

On first run choose **New configuration**, enter your email and app password, accept the defaults (or set your archive path), then use **Continue Processing → Resume** to begin archiving. You can safely stop at any time (Ctrl+C finishes the in-flight messages and checkpoints) and resume later.

---

## The menu

**Main menu**

1. **Test Connections** — guided IMAP/SMTP diagnostics with fix-it advice.
2. **Show Log / Data Summary** — archive totals plus a recovery/continuation report.
3. **Continue Processing** — pick a scope and archive:
   - Full extract, **Resume** (skip already-archived), Date range, New since last run (UID high-water), or a specific label/folder.
4. **Search the Archive** — senders, attachment names, attachment content, email text, or headers.
5. **Run Plugin** — execute drop-in `I_Run_Code_Against_Configuration` plugins.
6. **Switch Configuration** — open or create another account.
7. **Operations Subsystem**:
   - **Verify Files (resume readiness)** — integrity + confidence verdict (quick or deep).
   - **Edit Configuration File** — change fields; secrets stay masked.
   - **Retry Errors Found in Logs** — reprocess anything that failed.
   - **Analyze Recovery / Continuation** — drift check across manifest, records and index.
   - **Rebuild Manifest / Search Index From Tree** — self-heal from the record files.

---

## Script mode (headless / scheduled)

Run a plain-text `.ias` script non-interactively — ideal for Windows Task Scheduler:

```
Gmail_Archiver.exe -s "C:\path\to\daily.ias"
```

The account's configuration must already exist (create it once via the menu). Commands, one per line (`#` = comment):

| Command | Meaning |
| --- | --- |
| `EMAIL you@gmail.com` | Load + decrypt that account |
| `SCOPE FULL \| RESUME \| NEW` | Set the processing scope |
| `SCOPE DATERANGE 2020-01-01 -` | Date bounds (`-` = open end) |
| `SCOPE LABEL "[Gmail]/All Mail"` | A specific folder/label |
| `PROCESS` | Run the archival pipeline |
| `VERIFY [DEEP]` | File verification + verdict |
| `REBUILD MANIFEST \| INDEX` | Reconcile from the record tree |
| `SUMMARY` | Print the continuation report |

The process exit code is `0` on success, non-zero if any command failed (so schedulers can detect problems). A ready-to-edit sample is in `Resources/Examples/Scripts/example.ias`.

---

## How the archive is stored

For each account (folders come from the configuration):

```
ARCHIVES/<account>/            record tree, BYMONTH:  YYYY/MM/<pointer>.json
ARCHIVES/<account>/ATTACHMENTS/  <pointer>.zip  (one zip of a message's attachments)
INDEX/<account>/INDEX/         Lucene search index
<account>.manifest.json        counts, dedup set, pointer index, UID high-water (snapshot)
<account>.journal.log          append-only per-message journal (crash-safe resume)
<account>.failures.log         messages that failed, for retry
<account>.verification.json    last verification stamp (dated verdict)
```

Each record JSON is flat and self-describing, so search and rebuild never need the original transport. If the manifest or index is ever lost, **Rebuild … From Tree** reconstructs them from the record files.

---

## Configuration reference

Stored DPAPI-encrypted at `Resources/Configurations/<account>.json`. Key fields:

- `email`, `server.application_specific_password` — account + app password.
- `archive_folder`, `archive_index_folder`, `archive_attachments_folder` — where data lives.
- `archive_attachments` — capture attachments (default true).
- `index_email_*` — per-field index switches (subject/from/to/cc/bcc/body/attachments).
- **Performance:**
  - `max_emails_per_minute`, `max_attachment_size_in_MB_per_minute` — global rate ceilings.
  - `pause_between_requests_in_seconds` — per-connection spacing (default 11; lower to go faster).
  - `max_retries` — transport retry budget.
  - `fetch_connections` — parallel IMAP connections (default 4; Gmail allows up to 15).
  - `parse_workers` — parse/store worker threads (`0` = auto = CPU count, capped).

**Tuning tips:** the fetch is deliberately gentle by default. To speed up a large first extract, raise `fetch_connections` and/or lower `pause_between_requests_in_seconds`, keeping `max_emails_per_minute` as your safety ceiling. Parsing/attachment-text extraction is CPU-bound and scales with `parse_workers`.

---

## Troubleshooting

- **IMAP authentication failed** — the app password is wrong or was revoked, or 2-Step Verification isn't on. Generate a new 16-char app password and update it via Operations → Edit Configuration.
- **"Failed to decrypt … protected under a different account, machine, or OS"** — encrypted configs are tied to the account/machine that created them (Windows DPAPI) or to the local AES key file (Linux/macOS). Recreate the configuration on this machine. Don't copy configs between machines or OSes; recreate them.
- **Slow downloads** — see the tuning tips above; the 11-second default pause dominates.
- **Interrupted run** — just start again and choose **Resume**; the journal makes this exact. Run **Verify Files** any time to confirm the archive is safe to continue.

---

## Privacy

Everything is local. The archiver talks only to Gmail's IMAP/SMTP servers using your credentials; it sends your mail nowhere else and has no telemetry. Keep your archive folders and `Resources/Configurations` out of source control (see `.gitignore`).

---

## License

MIT — see [LICENSE](LICENSE). Note: bundled dependencies (MailKit, Lucene.NET, PdfPig, DocumentFormat.OpenXml, Newtonsoft.Json) carry their own permissive licenses.
