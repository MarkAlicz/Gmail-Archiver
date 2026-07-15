
using System;
using System.IO;

namespace IVolt.Core.Email.Configuration
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Cross-platform, per-user locations for configuration, secret keys, and the default archive
    /// root. Keeps the original Windows behavior (configs beside the executable under Resources) while
    /// giving Linux/macOS writable per-user directories, since a binary installed under /usr or /opt
    /// cannot write next to itself.
    /// </summary>
    ///
    /// <remarks>	I Volt, 7/3/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public static class App_Paths
    {
        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Per-user application data directory: %APPDATA%\IVolt on Windows, ~/.config/IVolt on
        /// Linux/macOS. Used for the AES key file (non-Windows) and per-user configs.
        /// </summary>
        ///
        /// <value>	The user data directory. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static string UserDataDir { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IVolt");

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Where account configurations are stored. Windows keeps the original portable location
        /// (Resources\Configurations beside the exe) for backward compatibility; Linux/macOS use a
        /// writable per-user directory.
        /// </summary>
        ///
        /// <value>	The configuration directory. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static string ConfigDirectory =>
            OperatingSystem.IsWindows()
                ? Path.Combine(AppContext.BaseDirectory, "Resources", "Configurations")
                : Path.Combine(UserDataDir, "Configurations");

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	OS-appropriate default base path suggested by the new-configuration wizard. </summary>
        ///
        /// <value>	The default archive base path. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static string DefaultArchiveBase =>
            OperatingSystem.IsWindows()
                ? @"C:\IVolt\Mail"
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ivolt", "mail");
    }
}
