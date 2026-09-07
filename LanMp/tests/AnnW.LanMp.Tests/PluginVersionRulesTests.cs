using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class PluginVersionRulesTests
    {
        [Fact]
        public void Same_version_compatible()
        {
            Assert.True(PluginVersionRules.IsCompatible("0.18.3", "0.18.3"));
            Assert.True(PluginVersionRules.IsCompatible(" 0.18.3 ", "0.18.3"));
        }

        [Fact]
        public void Different_or_empty_rejected()
        {
            Assert.False(PluginVersionRules.IsCompatible("0.18.3", "0.18.2"));
            Assert.False(PluginVersionRules.IsCompatible("0.18.3", ""));
            Assert.False(PluginVersionRules.IsCompatible("", "0.18.3"));
            Assert.False(PluginVersionRules.IsCompatible(null, "0.18.3"));
        }

        [Fact]
        public void Mismatch_message_lists_both_versions()
        {
            var msg = PluginVersionRules.FormatMismatchMessage("0.18.3", "0.18.1");
            Assert.Contains("加入失败", msg);
            Assert.Contains("版本不一致", msg);
            Assert.Contains("主机：0.18.3", msg);
            Assert.Contains("客机：0.18.1", msg);

            var reject = PluginVersionRules.MakeMismatchReject("0.18.3", "0.18.1");
            Assert.Equal((int)LobbyRejectCode.PluginVersionMismatch, reject.code);
            Assert.Equal(msg, reject.message);
            Assert.Equal("0.18.3", reject.hostPluginVersion);
            Assert.Equal("0.18.1", reject.guestPluginVersion);
        }

        [Fact]
        public void Unknown_placeholder_when_empty()
        {
            var msg = PluginVersionRules.FormatMismatchMessage("", "0.18.3");
            Assert.Contains("主机：(未知)", msg);
            Assert.Contains("客机：0.18.3", msg);
        }
    }
}
