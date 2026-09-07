using System;
using System.IO;
using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    /// <summary>
    /// Forget-proof: README / Hello default must track LanMpVersion.Current.
    /// </summary>
    public class LanMpVersionSyncTests
    {
        [Fact]
        public void NetSession_default_plugin_version_matches_LanMpVersion()
        {
            Assert.Equal(LanMpVersion.Current, new NetSession(new CollectingLanLogger()).LocalPluginVersion);
        }

        [Fact]
        public void Readme_documents_LanMpVersion_Current()
        {
            var root = FindLanMpRoot();
            var readme = Path.Combine(root, "README.md");
            Assert.True(File.Exists(readme), "missing " + readme);
            var text = File.ReadAllText(readme);
            Assert.Contains("**" + LanMpVersion.Current + "**", text);
            Assert.Contains("LanMpVersion", text);
        }

        [Fact]
        public void LanMpVersion_cs_is_single_source_file()
        {
            var root = FindLanMpRoot();
            var path = Path.Combine(root, "src", "AnnW.LanMp.Protocol", "LanMpVersion.cs");
            Assert.True(File.Exists(path), "missing " + path);
            var text = File.ReadAllText(path);
            Assert.Contains("Current = \"" + LanMpVersion.Current + "\"", text);
        }

        [Fact]
        public void Plugin_cs_aliases_LanMpVersion_Current()
        {
            var root = FindLanMpRoot();
            var path = Path.Combine(root, "src", "AnnW.LanMp", "Plugin.cs");
            Assert.True(File.Exists(path), "missing " + path);
            var text = File.ReadAllText(path);
            Assert.Contains("[BepInPlugin(PluginGuid, PluginName, LanMpVersion.Current)]", text);
            Assert.Contains("PluginVersion = LanMpVersion.Current", text);
            Assert.DoesNotContain("PluginVersion = \"", text);
        }

        private static string FindLanMpRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "README.md");
                var versionCs = Path.Combine(dir.FullName, "src", "AnnW.LanMp.Protocol", "LanMpVersion.cs");
                if (File.Exists(candidate) && File.Exists(versionCs))
                    return dir.FullName;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate LanMp root from " +
                                                 AppDomain.CurrentDomain.BaseDirectory);
        }
    }
}
