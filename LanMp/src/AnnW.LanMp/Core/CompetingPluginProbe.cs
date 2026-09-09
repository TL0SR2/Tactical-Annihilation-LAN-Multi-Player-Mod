using System;
using System.IO;
using AnnW.LanMp.Protocol;
using BepInEx;
using BepInEx.Bootstrap;

namespace AnnW.LanMp.Core
{
    /// <summary>Host-side probe for competing LAN plugins (PL0). Does not touch battle state.</summary>
    internal static class CompetingPluginProbe
    {
        /// <summary>
        /// Returns true when another LAN MP plugin is already loaded or present under BepInEx/plugins.
        /// </summary>
        internal static bool TryDetect(out string detectedLabel)
        {
            detectedLabel = null;

            try
            {
                foreach (var kv in Chainloader.PluginInfos)
                {
                    var guid = kv.Key;
                    if (CompetingPluginRules.IsConflictingGuid(guid))
                    {
                        detectedLabel = guid;
                        return true;
                    }

                    var info = kv.Value;
                    var loc = info?.Location;
                    if (CompetingPluginRules.IsConflictingPathOrFileName(loc))
                    {
                        detectedLabel = string.IsNullOrEmpty(loc) ? guid : Path.GetFileName(loc);
                        return true;
                    }
                }
            }
            catch
            {
                // Chainloader may be unavailable in odd host contexts; fall through to filesystem.
            }

            try
            {
                var root = Paths.PluginPath;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    return false;

                foreach (var file in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (!CompetingPluginRules.IsConflictingPathOrFileName(name) &&
                        !CompetingPluginRules.IsConflictingPathOrFileName(file))
                        continue;
                    // Ignore our own tree.
                    if (file.IndexOf("AnnW.LanMp", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    detectedLabel = name;
                    return true;
                }
            }
            catch
            {
                // Best-effort only.
            }

            return false;
        }
    }
}
