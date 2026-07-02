# WINDOWS ONLY UNTIL NEXT VERSION SORRY IT SUCKS I KNOW

# Gmail-Archiver
Archive All your Gmails Locally while indexing them and downloading all the attachments; which are indexed as well.  Tons of features and a plugin architecture for expansion.

# You will need to turn on IMAP and Create an App Password

See and Configure IMAP Settings in Gmail
https://mail.google.com/mail/u/2/#settings/fwdandpop

Setting up Signing In With App Passwords
https://support.google.com/accounts/answer/185833?hl=en&authuser=2

# 1. System Overview

The IVolt Gmail IMAP Archiver is a console-driven Windows utility that connects to a Gmail account, 
retrieves every message with full metadata and attachments, and writes a durable, searchable, 
offline archive. It is built for long-running bulk capture: launch Email_Archiver.exe, verify a 
configuration, then process the mailbox under controlled throttling until it is fully archived — 
with safe resume if interrupted.
Transport is handled by MailKit; all message parsing and the cryptographic integrity hash are 
performed by IVolt's own parser, preserving the exact wire octets of every message. Full-text 
search is powered by an embedded Lucene.NET index.
 Design principle 
The archiver certifies provenance and integrity of capture — a SHA-256 over each message's raw 
octets — never the truth of the content.

# 2. What's New in This Release

This consolidated release documents the complete processing engine now that it is built. The 
following subsystems are new or newly specified in concrete form:
• MailKit transport + custom parser split — MailKit fetches raw octets and native Gmail extensions; 
the IVolt parser owns all decoding and the provenance hash.
• Interactive engine — startup screen, verified-config main menu, and guided connection 
diagnostics.
• Scope selection — full extract, resume, date range, new-since-last-run (UID high-water), or a 
specific label.
• Persistent manifest — keyed on X-GM-MSGID for O(1) resume, de-duplication, and instant summaries; 
rebuildable from the archive tree.
• Lucene.NET search — field-scoped over senders, attachment names, attachment content, email text, 
and headers.
• Attachment text extraction — text, PDF, Word, Excel; images indexed by name and size only.
• Plugin framework — reflection-loaded I_Run_Code_Against_Configuration implementations.

# The Main Menu
After a configuration is verified, the main menu appears:
===== MAIN MENU =====
1. Test Connections
2. Show Log / Data Summary
3. Continue Processing Configuration File
4. Search the Archive
5. Run Plugin
6. Switch Configuration
7. Operations Subsystem
0. Exit

# Item Action
1. Test Connections Verify IMAP and SMTP with guided, type-specific failure diagnostics.
2. Show Log / Data Summary Message count, attachment count, date range, index docs — from the manifest.
3. Continue Processing Open the scope selector and run the archival fetch loop.
4. Search the Archive Field-scoped Lucene search over the local archive.
5. Run Plugin Discover and run I_Run_Code_Against_Configuration plugins.
6. Switch Configuration Load and verify a different account, then re-enter this menu.
7. Try new ways to recover error attempts.  Edit Configuration File. And More.

# Menu structure note
Search and Run Plugin are top-level operations on the active archive, so they never require
re-loading a configuration first. Switching accounts is its own explicit action.

# Plugins
Custom logic runs through the I_Run_Code_Against_Configuration interface. The loader discovers every active
implementation in loaded assemblies and in a Plugins folder beside the executable, orders them by
Execution_Order, and offers them under Run Plugin. Each plugin receives the loaded configuration, the archive
context (records, manifest, attachments, index), and a console writer for progress.
public interface I_Run_Code_Against_Configuration {
string Plugin_Name { get; }
string Plugin_Description { get; }
int Execution_Order { get; }
bool Active { get; }
bool Run(Configuration_Definition_Container config,
IArchiveContext archive, TextWriter output);
}

# Throttling, Retries & Resumability
IVOLT LLC Gmail IMAP Archiver — User Guide
IVolt LLC · 8 W Wilson St A1, Batavia, IL · ivolt.io · 630-358-9449 CONFIDENTIAL Page 10
Setting Default Effect
max_emails_per_minute 100 Fetch rate ceiling per rolling minute.
max_attachment_size_in_MB_per_minute 50 Download-volume ceiling per rolling minute.
pause_between_requests_in_seconds 11 Fixed delay between messages.
max_retries 3 Attempts per message with backoff before
skipping.
log_level DETAIL Operation log verbosity.
The loop checkpoints the index and manifest roughly every 25 messages, so an interruption loses at most a
handful of messages of progress. Because dedup is keyed on X-GM-MSGID, resuming simply skips what is
already archived.
