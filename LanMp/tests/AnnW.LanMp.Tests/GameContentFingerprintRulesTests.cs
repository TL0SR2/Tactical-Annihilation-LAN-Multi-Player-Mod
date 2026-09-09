using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class GameContentFingerprintRulesTests
    {
        [Fact]
        public void Same_fingerprint_compatible()
        {
            Assert.True(GameContentFingerprintRules.IsCompatible("ABCDef", "abcdef"));
            Assert.True(GameContentFingerprintRules.IsCompatible("  ab  ", "ab"));
        }

        [Fact]
        public void Both_empty_compatible_for_tests()
        {
            Assert.True(GameContentFingerprintRules.IsCompatible("", ""));
            Assert.True(GameContentFingerprintRules.IsCompatible(null, null));
        }

        [Fact]
        public void One_sided_or_mismatch_rejected()
        {
            Assert.False(GameContentFingerprintRules.IsCompatible("abc", ""));
            Assert.False(GameContentFingerprintRules.IsCompatible("", "abc"));
            Assert.False(GameContentFingerprintRules.IsCompatible("aaa", "bbb"));
        }

        [Fact]
        public void Mismatch_reject_payload()
        {
            var reject = GameContentFingerprintRules.MakeMismatchReject("hostfp0123456789abcd", "guestfp");
            Assert.Equal((int)LobbyRejectCode.ContentFingerprintMismatch, reject.code);
            Assert.Contains("指纹不一致", reject.message);
            Assert.Equal("hostfp0123456789abcd", reject.hostContentFingerprint);
            Assert.Equal("guestfp", reject.guestContentFingerprint);
        }
    }
}
