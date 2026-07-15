
using System;
using System.IO;
using System.Security.Cryptography;

namespace IVolt.Core.Email.Configuration
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>	Encrypts / decrypts small secrets (the configuration JSON) at rest. </summary>
    ///
    /// <remarks>	I Volt, 7/3/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public interface ISecretProtector
    {
        /// <summary>	Short scheme identifier written as the file header (e.g. "DPAPI1", "AESK1"). </summary>
        string SchemeId { get; }

        /// <summary>	Encrypts plaintext bytes. </summary>
        byte[] Protect(byte[] plaintext);

        /// <summary>	Decrypts ciphertext bytes produced by <see cref="Protect"/>. </summary>
        byte[] Unprotect(byte[] ciphertext);
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Platform-appropriate secret protection with a self-describing file format. Files are stored as
    /// "<c>SCHEME:base64</c>"; a file with no recognized header is treated as the original raw-base64
    /// Windows-DPAPI format, so existing configurations keep working. On Windows the default is DPAPI
    /// (transparent, tied to the Windows user); on Linux/macOS it is AES-256-GCM under a key file kept
    /// (owner-only) in the per-user data directory.
    ///
    /// Security note: the AES key file is the root of trust on Linux/macOS. Treat it like an SSH
    /// private key — if it is lost, encrypted configurations cannot be recovered. It is created with
    /// 0600 permissions.
    /// </summary>
    ///
    /// <remarks>	I Volt, 7/3/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public static class Secret_Protector
    {
        private static readonly Dpapi_Protector Dpapi = new Dpapi_Protector();
        private static readonly Aes_Keyfile_Protector Aes = new Aes_Keyfile_Protector();

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	The protector used to WRITE new secrets on this platform. </summary>
        ///
        /// <value>	The default protector. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static ISecretProtector Default =>
            OperatingSystem.IsWindows() ? (ISecretProtector)Dpapi : Aes;

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Encrypts plaintext and returns the storable "SCHEME:base64" string. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="plaintext">	The plaintext bytes. </param>
        ///
        /// <returns>	The stored representation. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static string Protect(byte[] plaintext)
        {
            var p = Default;
            byte[] cipher = p.Protect(plaintext);
            return p.SchemeId + ":" + Convert.ToBase64String(cipher);
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Decrypts a stored "SCHEME:base64" string (or a legacy headerless DPAPI file). </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="stored">	The stored representation. </param>
        ///
        /// <returns>	The recovered plaintext bytes. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static byte[] Unprotect(string stored)
        {
            if (stored == null) throw new ArgumentNullException(nameof(stored));
            stored = stored.Trim();

            // Base64 never contains ':', so a colon reliably marks the scheme header.
            int idx = stored.IndexOf(':');
            if (idx > 0)
            {
                string scheme = stored.Substring(0, idx);
                byte[] cipher = Convert.FromBase64String(stored.Substring(idx + 1));
                if (string.Equals(scheme, Dpapi.SchemeId, StringComparison.OrdinalIgnoreCase)) return Dpapi.Unprotect(cipher);
                if (string.Equals(scheme, Aes.SchemeId, StringComparison.OrdinalIgnoreCase)) return Aes.Unprotect(cipher);
                throw new CryptographicException($"Unknown secret scheme '{scheme}'.");
            }

            // Legacy format: raw base64 of DPAPI-protected bytes.
            return Dpapi.Unprotect(Convert.FromBase64String(stored));
        }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>	Windows DPAPI (CurrentUser) protector. </summary>
    ///
    /// <remarks>	I Volt, 7/3/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    internal sealed class Dpapi_Protector : ISecretProtector
    {
        public string SchemeId => "DPAPI1";

        public byte[] Protect(byte[] plaintext)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("DPAPI protection is only available on Windows.");
            return ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }

        public byte[] Unprotect(byte[] ciphertext)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "This configuration was encrypted with Windows DPAPI and cannot be read on this OS. " +
                    "Recreate the configuration here (New configuration).");
            return ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
    }

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// AES-256-GCM protector keyed by an owner-only key file in the per-user data directory. Ciphertext
    /// layout: nonce(12) || tag(16) || ciphertext.
    /// </summary>
    ///
    /// <remarks>	I Volt, 7/3/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    internal sealed class Aes_Keyfile_Protector : ISecretProtector
    {
        private const int NonceLen = 12;
        private const int TagLen = 16;
        private const int KeyLen = 32;

        private readonly object _lock = new object();
        private byte[] _key;

        public string SchemeId => "AESK1";

        private byte[] Key()
        {
            if (_key != null) return _key;
            lock (_lock)
            {
                if (_key != null) return _key;
                _key = LoadOrCreateKey();
                return _key;
            }
        }

        public byte[] Protect(byte[] plaintext)
        {
            byte[] key = Key();
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceLen);
            byte[] cipher = new byte[plaintext.Length];
            byte[] tag = new byte[TagLen];

            using (var aes = new AesGcm(key, TagLen))
                aes.Encrypt(nonce, plaintext, cipher, tag);

            byte[] outp = new byte[NonceLen + TagLen + cipher.Length];
            Buffer.BlockCopy(nonce, 0, outp, 0, NonceLen);
            Buffer.BlockCopy(tag, 0, outp, NonceLen, TagLen);
            Buffer.BlockCopy(cipher, 0, outp, NonceLen + TagLen, cipher.Length);
            return outp;
        }

        public byte[] Unprotect(byte[] blob)
        {
            if (blob == null || blob.Length < NonceLen + TagLen)
                throw new CryptographicException("Ciphertext is too short or corrupt.");

            byte[] key = Key();
            byte[] nonce = new byte[NonceLen];
            byte[] tag = new byte[TagLen];
            int cipherLen = blob.Length - NonceLen - TagLen;
            byte[] cipher = new byte[cipherLen];
            Buffer.BlockCopy(blob, 0, nonce, 0, NonceLen);
            Buffer.BlockCopy(blob, NonceLen, tag, 0, TagLen);
            Buffer.BlockCopy(blob, NonceLen + TagLen, cipher, 0, cipherLen);

            byte[] plain = new byte[cipherLen];
            using (var aes = new AesGcm(key, TagLen))
                aes.Decrypt(nonce, cipher, tag, plain);   // throws CryptographicException on tamper / wrong key
            return plain;
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Loads the 32-byte key, creating an owner-only key file on first use. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <returns>	The key bytes. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static byte[] LoadOrCreateKey()
        {
            string dir = App_Paths.UserDataDir;
            Directory.CreateDirectory(dir);
            string keyPath = Path.Combine(dir, "secret.key");

            if (File.Exists(keyPath))
            {
                byte[] k = File.ReadAllBytes(keyPath);
                if (k.Length == KeyLen) return k;
                throw new CryptographicException(
                    $"Key file '{keyPath}' is corrupt (expected {KeyLen} bytes). " +
                    "If it was lost or changed, existing configurations cannot be decrypted.");
            }

            byte[] key = RandomNumberGenerator.GetBytes(KeyLen);
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(keyPath, key); // Windows uses DPAPI, but keep this path safe anyway
            }
            else
            {
                using (var fs = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(key, 0, key.Length);
                    fs.Flush();
                }
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600
            }
            return key;
        }
    }
}
