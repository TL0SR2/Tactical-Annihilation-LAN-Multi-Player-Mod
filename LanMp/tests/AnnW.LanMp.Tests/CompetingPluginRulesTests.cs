using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class CompetingPluginRulesTests
    {
        [Theory]
        [InlineData("xingyistarry.mp", true)]
        [InlineData("XingyiStarry.Mp", true)]
        [InlineData("com.xingyistarry.mp.extra", true)]
        [InlineData("annw.lanmp", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Guid_conflict_detection(string guid, bool expected)
        {
            Assert.Equal(expected, CompetingPluginRules.IsConflictingGuid(guid));
        }

        [Theory]
        [InlineData("XingyiStarry.Mp.dll", true)]
        [InlineData(@"BepInEx\plugins\XingyiStarry.Mp\XingyiStarry.Mp.Protocol.dll", true)]
        [InlineData("AnnW.LanMp.dll", false)]
        public void Path_conflict_detection(string path, bool expected)
        {
            Assert.Equal(expected, CompetingPluginRules.IsConflictingPathOrFileName(path));
        }

        [Fact]
        public void Conflict_message_mentions_both_plugins()
        {
            var msg = CompetingPluginRules.FormatConflictMessage("xingyistarry.mp");
            Assert.Contains("xingyistarry.mp", msg);
            Assert.Contains("AnnW.LanMp", msg);
            Assert.Contains("XingyiStarry.Mp", msg);
        }
    }
}
