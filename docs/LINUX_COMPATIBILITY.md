# Cross-platform notes (Linux / macOS)

The archiver targets `net10.0` and runs on Windows, Linux, and macOS. Every third-party dependency (MailKit, Lucene.NET, PdfPig, DocumentFormat.OpenXml, Newtonsoft.Json) is cross-platform. The only platform-specific concern is how account credentials are encrypted at rest, plus a couple of path defaults — both handled below.

## Secret protection at rest

Configurations (which contain your Gmail app password) are always encrypted before hitting disk. The scheme is chosen per-OS behind the `ISecretProtector` abstraction (`Configuration/Secret_Protector.cs`):

| Platform | Scheme | Key material |
| --- | --- | --- |
| Windows | DPAPI (`CurrentUser`) | Managed by Windows, tied to your Windows login |
| Linux / macOS | AES-256-GCM | 32-byte key in an owner-only (`0600`) key file |

Files are written as `SCHEME:base64` (`DPAPI1:` or `AESK1:`). A file with **no** header is treated as the original raw-base64 DPAPI format, so pre-existing Windows configurations keep working after upgrade.

### The Linux/macOS key file

- Location: `~/.config/IVolt/secret.key` (created on first use, permissions `0600`).
- It is the root of trust — **treat it like an SSH private key.** If it is lost or changed, existing encrypted configurations cannot be decrypted; you'd recreate them.
- Back it up if you back up your configs. Do **not** commit it (the repo `.gitignore` already excludes secrets).
- macOS uses the same AES-key-file scheme; if you prefer Keychain integration, implement a third `ISecretProtector` and make it the default in `Secret_Protector.Default` for macOS. (Same hook exists for libsecret/Secret Service on Linux.)

Encrypted configs are **not portable between machines or OSes** (this was already true under DPAPI). Recreate them per machine.

## File locations

`Configuration/App_Paths.cs` centralizes locations:

| | Windows | Linux / macOS |
| --- | --- | --- |
| Config directory | `…\Resources\Configurations` (beside the exe, unchanged) | `~/.config/IVolt/Configurations` |
| Key file / app data | n/a (DPAPI) | `~/.config/IVolt/` |
| Default archive base (wizard) | `C:\IVolt\Mail` | `~/.local/share/ivolt/mail` |

Windows behavior is intentionally unchanged (portable, config beside the binary). On Linux a binary in `/usr/bin` or `/opt` can't write next to itself, so a per-user directory is used. You can always override the archive paths in the new-configuration wizard or via **Operations → Edit Configuration**.

## Build & run on Linux

```
dotnet build Gmail_Archiver_Solution.slnx -c Release
dotnet run --project Gmail_Archiver -c Release        # interactive
dotnet run --project Gmail_Archiver -c Release -- -s ~/daily.ias   # script mode
```

To produce a self-contained binary:

```
dotnet publish Gmail_Archiver -c Release -r linux-x64  --self-contained
dotnet publish Gmail_Archiver -c Release -r osx-arm64  --self-contained
```

## Terminal / UI

The live download dashboard (`Download_Monitor`) uses ANSI escape codes, which Linux/macOS terminals support natively. It only calls the Windows `kernel32` VT-enable P/Invoke when running on Windows (guarded), and falls back to a single-line status when output is redirected (piped to a file or another process).

## Known lower-priority items

- **Case-sensitive filesystems.** Linux paths are case-sensitive. `Archive_Store.VerifyFiles` compares record paths with `OrdinalIgnoreCase`; on Linux this is harmless in practice (paths are generated, not user-typed) but could be made OS-aware. Attachment filenames that differ only in case are distinct files on Linux and collide on Windows — the archiver de-collides with a numeric prefix, so this is not a data-loss risk.
- **Cross-OS archive portability.** Stored relative paths use the creating OS's separator. Reading a Windows-created archive on Linux would need separator normalization on load; only relevant if you physically move an archive between operating systems.
- **`[STAThread]`** was removed from `Main` (it's a Windows-COM apartment hint with no effect here and no meaning on Unix).
