
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using IVolt.Core.Interfaces.Gmail;

namespace IVolt.Core.Email.Gmail.Plugins
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Discovers I_Run_Code_Against_Configuration implementations. Scans loaded assemblies plus any
    /// DLLs in a "Plugins" folder next to the executable, instantiates each active plugin, and
    /// returns them ordered by Execution_Order (then name). Failures to load a given assembly are
    /// reported and skipped, never fatal.
    /// </summary>
    ///
    /// <remarks>	I Volt, 6/30/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public static class Plugin_Loader
    {
        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Loads all. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="output">	The output. </param>
        ///
        /// <returns>	all. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static List<I_Run_Code_Against_Configuration> LoadAll(TextWriter output)
        {
            var found = new List<I_Run_Code_Against_Configuration>();
            var assemblies = new List<Assembly>(AppDomain.CurrentDomain.GetAssemblies());

            // Also probe a Plugins folder beside the exe.
            string pluginDir = Path.Combine(AppContext.BaseDirectory, "Plugins");
            if (Directory.Exists(pluginDir))
            {
                foreach (var dll in Directory.EnumerateFiles(pluginDir, "*.dll"))
                {
                    try { assemblies.Add(Assembly.LoadFrom(dll)); }
                    catch (Exception ex) { output.WriteLine($"  ! Could not load plugin assembly '{Path.GetFileName(dll)}': {ex.Message}"); }
                }
            }

            var iface = typeof(I_Run_Code_Against_Configuration);
            foreach (var asm in assemblies.Distinct())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract || t.IsInterface) continue;
                    if (!iface.IsAssignableFrom(t)) continue;
                    try
                    {
                        var inst = (I_Run_Code_Against_Configuration)Activator.CreateInstance(t);
                        if (inst != null && inst.Active) found.Add(inst);
                    }
                    catch (Exception ex)
                    {
                        output.WriteLine($"  ! Could not instantiate plugin '{t.FullName}': {ex.Message}");
                    }
                }
            }

            return found
                .OrderBy(p => p.Execution_Order)
                .ThenBy(p => p.Plugin_Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
