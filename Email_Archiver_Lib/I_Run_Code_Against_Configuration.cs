
using System.IO;

using IVolt.Core.Email.Configuration;
using IVolt.Core.Email.Gmail;          // IArchiveContext lives here (Archive_Model.cs)

namespace IVolt.Core.Interfaces.Gmail
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Contract implemented by drop-in plugins that run custom logic against a loaded configuration
    /// and its local archive. The engine discovers every concrete implementation via reflection (see
    /// Plugin_Loader) and offers them under the "Run Plugin" menu, ordered by Execution_Order.
    /// Follows IVolt plugin-manager conventions (name, description, execution order, active flag).
    /// </summary>
    ///
    /// <remarks>	I Volt, 6/30/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public interface I_Run_Code_Against_Configuration
    {
        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Short human-readable plugin name shown in the menu. </summary>
        ///
        /// <value>	The name of the plugin. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        string Plugin_Name { get; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	One-line description of what the plugin does. </summary>
        ///
        /// <value>	Information describing the plugin. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        string Plugin_Description { get; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Relative ordering in the plugin list (ascending). Ties broken by name. </summary>
        ///
        /// <value>	The execution order. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        int Execution_Order { get; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	When false the loader ignores the plugin entirely. </summary>
        ///
        /// <value>	True if active, false if not. </value>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        bool Active { get; }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Executes the plugin. </summary>
        ///
        /// <param name="config"> 	The loaded, decrypted configuration. </param>
        /// <param name="archive">	Access to the local archive: stored records, manifest, attachments,
        /// 						index. </param>
        /// <param name="output"> 	Console writer so the plugin can report progress / hold the user's
        /// 						hand. </param>
        ///
        /// <returns>	True on success; false if the plugin reported a handled failure. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        bool Run(Configuration_Definition_Container config, IArchiveContext archive, TextWriter output);
    }
}
