using System;
using System.Globalization;

namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Encodes Guest UX-only context into Intent/Command <c>extrasJson</c> so Host
    /// Validate + ExecuteAction can rebind state that never lives on the board alone.
    ///
    /// Formats (backward compatible):
    /// - bare string / <c>t:Name</c> → BUILD/TRAIN unit SD name (legacy bare string)
    /// - <c>u:123</c> → UNLOAD_SINGLE cargo <c>unit_id</c> (binds <c>GS_Battle.ux_unload_unit</c>)
    /// </summary>
    public static class ActionExtrasCodec
    {
        public const string UnloadPrefix = "u:";
        public const string TemplatePrefix = "t:";

        public static string FromTrainTemplate(string unitSdName) =>
            string.IsNullOrEmpty(unitSdName) ? "" : unitSdName;

        public static string FromUnloadUnitId(int unitId) =>
            UnloadPrefix + unitId.ToString(CultureInfo.InvariantCulture);

        public static bool TryGetUnloadUnitId(string extras, out int unitId)
        {
            unitId = -1;
            if (string.IsNullOrEmpty(extras) ||
                extras.Length <= UnloadPrefix.Length ||
                !extras.StartsWith(UnloadPrefix, StringComparison.Ordinal))
                return false;
            return int.TryParse(
                       extras.Substring(UnloadPrefix.Length),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out unitId) &&
                   unitId >= 0;
        }

        public static bool TryGetTrainTemplateId(string extras, out string templateId)
        {
            templateId = null;
            if (string.IsNullOrEmpty(extras))
                return false;
            if (extras.StartsWith(UnloadPrefix, StringComparison.Ordinal))
                return false;
            if (extras.StartsWith(TemplatePrefix, StringComparison.Ordinal))
            {
                templateId = extras.Substring(TemplatePrefix.Length);
                return !string.IsNullOrEmpty(templateId);
            }
            templateId = extras;
            return true;
        }

        /// <summary>Vanilla ActionCate values that need train_template before CanDoAction/DoActionCell.</summary>
        public static bool NeedsTrainTemplate(int actionCate) =>
            actionCate == 2 || // BUILD
            actionCate == 4;   // TRAIN

        /// <summary>Vanilla ActionCate.UNLOAD_SINGLE — needs GS_Battle.ux_unload_unit.</summary>
        public static bool NeedsUnloadUnit(int actionCate) =>
            actionCate == 9;
    }
}
