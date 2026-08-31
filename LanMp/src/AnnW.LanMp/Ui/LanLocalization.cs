using ANNW;
using HarmonyLib;
using UnityEngine;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Registers LanMp UI strings into the game's <see cref="SD_LAN_LAN"/> table and binds
    /// <see cref="Localized_Txt"/> the same way vanilla menu buttons do (cate + key → LAN.Get).
    /// </summary>
    internal static class LanLocalization
    {
        /// <summary>Matches vanilla Localized_Txt default cate.</summary>
        public const string Cate = "UI";

        /// <summary>Key only; complex id is Cate_Key → UI_LanMp_Lobby.</summary>
        public const string KeyLobby = "LanMp_Lobby";
        public const string KeyHost = "LanMp_Host";
        public const string KeyJoin = "LanMp_Join";
        public const string KeyReady = "LanMp_Ready";
        public const string KeyStart = "LanMp_Start";
        public const string KeyLeave = "LanMp_Leave";
        public const string KeyClose = "LanMp_Close";

        public const string ComplexKeyLobby = Cate + "_" + KeyLobby;

        public const string CnLobby = "多人联机大厅";
        public const string EnLobby = "LAN Multiplayer";

        private static bool _logged;

        public static void EnsureRegistered()
        {
            UpsertLan(ComplexKeyLobby, Cate, KeyLobby, CnLobby, EnLobby);
            UpsertLan(Cate + "_" + KeyHost, Cate, KeyHost, "创建房间", "Create Room");
            UpsertLan(Cate + "_" + KeyJoin, Cate, KeyJoin, "加入房间", "Join Room");
            UpsertLan(Cate + "_" + KeyReady, Cate, KeyReady, "准备", "Ready");
            UpsertLan(Cate + "_" + KeyStart, Cate, KeyStart, "开始战斗", "Start Battle");
            UpsertLan(Cate + "_" + KeyLeave, Cate, KeyLeave, "离开房间", "Leave");
            UpsertLan(Cate + "_" + KeyClose, Cate, KeyClose, "关闭", "Close");
            UpsertLan(Cate + "_LanMp_Room", Cate, "LanMp_Room", "联机房间", "LAN Room");

            if (!_logged)
            {
                _logged = true;
                var ok = LAN.Has(Cate, KeyLobby);
                LanMpPlugin.Log?.LogInfo($"[UI] LAN registered {ComplexKeyLobby} Has={ok} sample={LAN.Get(Cate, KeyLobby)}");
            }
        }

        public static void BindLobbyButton(GameObject root)
        {
            if (root == null)
                return;

            EnsureRegistered();

            var locs = root.GetComponentsInChildren<Localized_Txt>(true);
            if (locs == null || locs.Length == 0)
            {
                var tmp = root.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                var host = tmp != null ? tmp.gameObject : root;
                var wasActive = host.activeSelf;
                host.SetActive(false);
                var created = host.AddComponent<Localized_Txt>();
                WireLocalizedTxt(created, tmp);
                host.SetActive(wasActive);
                locs = new[] { created };
                LanMpPlugin.Log?.LogInfo("[UI] Re-added Localized_Txt on " + host.name);
            }

            foreach (var loc in locs)
            {
                if (loc == null)
                    continue;
                loc.cate = Cate;
                loc.key = KeyLobby;
                loc.prefix = null;
                loc.endfix = null;
                var tmp = loc.GetComponent<TMPro.TextMeshProUGUI>()
                          ?? loc.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                WireLocalizedTxt(loc, tmp);
                try { loc.RenderLocalizedContent(); }
                catch (System.Exception ex)
                {
                    LanMpPlugin.Log?.LogWarning("[UI] BindLobbyButton Render: " + ex.Message);
                    if (tmp != null)
                        tmp.text = LAN.Get(Cate, KeyLobby);
                }
            }
        }

        private static void WireLocalizedTxt(Localized_Txt loc, TMPro.TextMeshProUGUI tmp)
        {
            if (loc == null)
                return;
            try
            {
                var f = AccessTools.Field(typeof(Localized_Txt), "txt_ugui");
                f?.SetValue(loc, tmp);
                var f2 = AccessTools.Field(typeof(Localized_Txt), "txt");
                f2?.SetValue(loc, null);
            }
            catch
            {
                // ignore
            }
        }

        private static void UpsertLan(string complexKey, string cate, string key, string cn, string en)
        {
            SD_LAN_LAN entry;
            if (SDBase<SD_LAN_LAN>.Has(complexKey, alert: false))
            {
                entry = SDBase<SD_LAN_LAN>.Get(complexKey, alert: false);
            }
            else
            {
                entry = new SD_LAN_LAN();
                SDBase<SD_LAN_LAN>.dic[complexKey] = entry;
            }

            entry.name = complexKey;
            entry.cate = cate;
            entry.key = key;
            entry.cn = cn;
            entry.en = en;
        }
    }
}
